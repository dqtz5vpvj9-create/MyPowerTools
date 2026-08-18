using System.Globalization;
using Microsoft.Data.Sqlite;

namespace InputMonitor.Core;

public sealed class EventRepository
{
    private static readonly CultureInfo DayCulture = CultureInfo.InvariantCulture;
    private readonly MonitorDatabase _db;
    private readonly AppCategoryMap _categories;

    public EventRepository(MonitorDatabase db, AppCategoryMap categories)
    {
        _db = db;
        _categories = categories;
    }

    public static string DayString(DateTimeOffset date) =>
        date.ToLocalTime().ToString("yyyy-MM-dd", DayCulture);

    public static (double Start, double End) DayRange(string day)
    {
        if (!DateTime.TryParseExact(
                day,
                "yyyy-MM-dd",
                DayCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.NoCurrentDateDefault,
                out var parsed))
        {
            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            return (now, now);
        }

        var start = new DateTimeOffset(parsed.Date, TimeZoneInfo.Local.GetUtcOffset(parsed.Date));
        var end = start.AddDays(1);
        return (start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
    }

    public void InsertEvents(IReadOnlyList<InputEventRecord> records)
    {
        var events = records.Where(record => record.Kind != InputEventKind.MouseMoveSample).ToArray();
        if (events.Length == 0)
        {
            return;
        }

        _db.Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO events (kind, ts, x, y, key_code, characters, modifiers, scroll_delta, is_auto_repeat)
                VALUES ($kind, $ts, $x, $y, $key, $chars, $mods, $scroll, $repeat);
                """;
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            var ts = command.Parameters.Add("$ts", SqliteType.Real);
            var x = command.Parameters.Add("$x", SqliteType.Real);
            var y = command.Parameters.Add("$y", SqliteType.Real);
            var key = command.Parameters.Add("$key", SqliteType.Integer);
            var chars = command.Parameters.Add("$chars", SqliteType.Text);
            var mods = command.Parameters.Add("$mods", SqliteType.Integer);
            var scroll = command.Parameters.Add("$scroll", SqliteType.Integer);
            var repeat = command.Parameters.Add("$repeat", SqliteType.Integer);
            foreach (var record in events)
            {
                kind.Value = InputEventKinds.ToStorage(record.Kind);
                ts.Value = ToUnix(record.WallTime);
                x.Value = (object?)record.X ?? DBNull.Value;
                y.Value = (object?)record.Y ?? DBNull.Value;
                key.Value = (object?)record.KeyCode ?? DBNull.Value;
                chars.Value = (object?)record.Characters ?? DBNull.Value;
                mods.Value = (long)record.Modifiers;
                scroll.Value = record.ScrollDelta;
                repeat.Value = record.IsAutoRepeat ? 1 : 0;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    public void InsertTrackPoints(IReadOnlyList<InputEventRecord> records)
    {
        var points = records.Where(record => record.Kind == InputEventKind.MouseMoveSample).ToArray();
        if (points.Length == 0)
        {
            return;
        }

        _db.Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO track_points (ts, x, y, move_delta) VALUES ($ts, $x, $y, $delta);";
            var ts = command.Parameters.Add("$ts", SqliteType.Real);
            var x = command.Parameters.Add("$x", SqliteType.Real);
            var y = command.Parameters.Add("$y", SqliteType.Real);
            var delta = command.Parameters.Add("$delta", SqliteType.Real);
            foreach (var record in points)
            {
                ts.Value = ToUnix(record.WallTime);
                x.Value = record.X ?? 0;
                y.Value = record.Y ?? 0;
                delta.Value = record.MoveDelta;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    public void InsertAppSession(FrontAppSession session)
    {
        if (session.End is not { } end)
        {
            return;
        }

        var duration = (end - session.Start).TotalSeconds;
        if (duration < 1)
        {
            return;
        }

        _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_usage (bundle_id, app_name, window_title, category, start_ts, end_ts, duration)
                VALUES ($bundle, $name, $title, $category, $start, $end, $duration);
                """;
            command.Parameters.AddWithValue("$bundle", session.BundleId);
            command.Parameters.AddWithValue("$name", session.AppName);
            command.Parameters.AddWithValue("$title", (object?)session.WindowTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$category", AppCategories.ToStorage(session.Category));
            command.Parameters.AddWithValue("$start", ToUnix(session.Start));
            command.Parameters.AddWithValue("$end", ToUnix(end));
            command.Parameters.AddWithValue("$duration", duration);
            command.ExecuteNonQuery();
        });
    }

    public void MergeDailyStats(string day, DaySummary delta)
    {
        _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO daily_stats (day, key_count, click_count, scroll_count, key_duration_ms, move_distance, active_input_seconds, active_app_seconds, updated_at)
                VALUES ($day, $keys, $clicks, $scrolls, $hold, $move, $input, $app, $updated)
                ON CONFLICT(day) DO UPDATE SET
                    key_count = key_count + excluded.key_count,
                    click_count = click_count + excluded.click_count,
                    scroll_count = scroll_count + excluded.scroll_count,
                    key_duration_ms = key_duration_ms + excluded.key_duration_ms,
                    move_distance = move_distance + excluded.move_distance,
                    active_input_seconds = active_input_seconds + excluded.active_input_seconds,
                    active_app_seconds = active_app_seconds + excluded.active_app_seconds,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$day", day);
            command.Parameters.AddWithValue("$keys", delta.KeyCount);
            command.Parameters.AddWithValue("$clicks", delta.ClickCount);
            command.Parameters.AddWithValue("$scrolls", delta.ScrollCount);
            command.Parameters.AddWithValue("$hold", delta.KeyDurationMs);
            command.Parameters.AddWithValue("$move", delta.MoveDistance);
            command.Parameters.AddWithValue("$input", delta.ActiveInputSeconds);
            command.Parameters.AddWithValue("$app", delta.ActiveAppSeconds);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        });
    }

    public DaySummary DaySummaryFor(string day)
    {
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT key_count, click_count, scroll_count, key_duration_ms, move_distance, active_input_seconds, active_app_seconds
                FROM daily_stats WHERE day = $day;
                """;
            command.Parameters.AddWithValue("$day", day);
            using var reader = command.ExecuteReader();
            var summary = new DaySummary { Day = day };
            if (reader.Read())
            {
                summary.KeyCount = reader.GetInt32(0);
                summary.ClickCount = reader.GetInt32(1);
                summary.ScrollCount = reader.GetInt32(2);
                summary.KeyDurationMs = reader.GetInt64(3);
                summary.MoveDistance = reader.GetDouble(4);
                summary.ActiveInputSeconds = reader.GetDouble(5);
                summary.ActiveAppSeconds = reader.GetDouble(6);
            }

            return summary;
        });
    }

    public Dictionary<string, DaySummary> DaySummaries(string startDay, string endDay)
    {
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT day, key_count, click_count, scroll_count, key_duration_ms, move_distance, active_input_seconds, active_app_seconds
                FROM daily_stats WHERE day BETWEEN $start AND $end;
                """;
            command.Parameters.AddWithValue("$start", startDay);
            command.Parameters.AddWithValue("$end", endDay);
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, DaySummary>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var day = reader.GetString(0);
                result[day] = new DaySummary
                {
                    Day = day,
                    KeyCount = reader.GetInt32(1),
                    ClickCount = reader.GetInt32(2),
                    ScrollCount = reader.GetInt32(3),
                    KeyDurationMs = reader.GetInt64(4),
                    MoveDistance = reader.GetDouble(5),
                    ActiveInputSeconds = reader.GetDouble(6),
                    ActiveAppSeconds = reader.GetDouble(7)
                };
            }

            return result;
        });
    }

    public Dictionary<int, int> HourlyCounts(string day, IReadOnlyList<string> kinds)
    {
        if (kinds.Count == 0)
        {
            return [];
        }

        var (start, end) = DayRange(day);
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT CAST(strftime('%H', datetime(ts, 'unixepoch', 'localtime')) AS INTEGER) AS hour, COUNT(*)
                FROM events
                WHERE ts BETWEEN $start AND $end AND kind IN ({Placeholders(kinds.Count)}) AND is_auto_repeat = 0
                GROUP BY hour;
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            BindKinds(command, kinds);
            return ReadIntMap(command);
        });
    }

    public Dictionary<int, double> HourlyAppSeconds(string day, AppCategory? categoryFilter = null)
    {
        var (start, end) = DayRange(day);
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT bundle_id,
                       CAST(strftime('%H', datetime(start_ts, 'unixepoch', 'localtime')) AS INTEGER) AS hour,
                       SUM(duration)
                FROM app_usage
                WHERE start_ts BETWEEN $start AND $end
                GROUP BY bundle_id, hour;
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            using var reader = command.ExecuteReader();
            var result = new Dictionary<int, double>();
            while (reader.Read())
            {
                if (!MatchesCategory(reader.GetString(0), categoryFilter))
                {
                    continue;
                }

                var hour = reader.GetInt32(1);
                result[hour] = result.GetValueOrDefault(hour) + reader.GetDouble(2);
            }

            return result;
        });
    }

    public Dictionary<string, double> PerDayAppSeconds(string startDay, string endDay, AppCategory? categoryFilter = null)
    {
        var start = DayRange(startDay).Start;
        var end = DayRange(endDay).End;
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT bundle_id,
                       strftime('%Y-%m-%d', datetime(start_ts, 'unixepoch', 'localtime')) AS day,
                       SUM(duration)
                FROM app_usage
                WHERE start_ts BETWEEN $start AND $end
                GROUP BY bundle_id, day;
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (!MatchesCategory(reader.GetString(0), categoryFilter))
                {
                    continue;
                }

                var day = reader.GetString(1);
                result[day] = result.GetValueOrDefault(day) + reader.GetDouble(2);
            }

            return result;
        });
    }

    public Dictionary<string, double> AppSecondsByDay(string startDay, string endDay)
    {
        var start = DayRange(startDay).Start;
        var end = DayRange(endDay).End;
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT strftime('%Y-%m-%d', datetime(start_ts, 'unixepoch', 'localtime')) AS day, SUM(duration)
                FROM app_usage
                WHERE start_ts BETWEEN $start AND $end
                GROUP BY day;
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            return ReadStringDoubleMap(command);
        });
    }

    public IReadOnlyList<(int Minute, int Count)> PerMinuteCounts(string day, IReadOnlyList<string> kinds)
    {
        if (kinds.Count == 0)
        {
            return [];
        }

        var (start, end) = DayRange(day);
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT (CAST(strftime('%H', datetime(ts, 'unixepoch', 'localtime')) AS INTEGER) * 60
                        + CAST(strftime('%M', datetime(ts, 'unixepoch', 'localtime')) AS INTEGER)) AS minute,
                       COUNT(*)
                FROM events
                WHERE ts BETWEEN $start AND $end AND kind IN ({Placeholders(kinds.Count)}) AND is_auto_repeat = 0
                GROUP BY minute
                ORDER BY minute;
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            BindKinds(command, kinds);
            return ReadMinuteCounts(command);
        });
    }

    public IReadOnlyList<(int Minute, int Count)> PerMinuteSessionStarts(string day)
    {
        var (start, end) = DayRange(day);
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (CAST(strftime('%H', datetime(start_ts, 'unixepoch', 'localtime')) AS INTEGER) * 60
                        + CAST(strftime('%M', datetime(start_ts, 'unixepoch', 'localtime')) AS INTEGER)) AS minute,
                       COUNT(*)
                FROM app_usage
                WHERE start_ts BETWEEN $start AND $end
                GROUP BY minute
                ORDER BY minute;
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            return ReadMinuteCounts(command);
        });
    }

    public Dictionary<string, int> PerDayOperationCounts(string startDay, string endDay, IReadOnlyList<string> kinds)
    {
        if (kinds.Count == 0)
        {
            return [];
        }

        var start = DayRange(startDay).Start;
        var end = DayRange(endDay).End;
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT strftime('%Y-%m-%d', datetime(ts, 'unixepoch', 'localtime')) AS day, COUNT(*)
                FROM events
                WHERE ts BETWEEN $start AND $end AND kind IN ({Placeholders(kinds.Count)}) AND is_auto_repeat = 0
                GROUP BY day;
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            BindKinds(command, kinds);
            return ReadStringIntMap(command);
        });
    }

    public Dictionary<string, int> PerDaySessionStarts(string startDay, string endDay)
    {
        var start = DayRange(startDay).Start;
        var end = DayRange(endDay).End;
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT strftime('%Y-%m-%d', datetime(start_ts, 'unixepoch', 'localtime')) AS day, COUNT(*)
                FROM app_usage
                WHERE start_ts BETWEEN $start AND $end
                GROUP BY day;
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            return ReadStringIntMap(command);
        });
    }

    public Dictionary<int, double> HourlyEventActiveSeconds(string day, IReadOnlyList<string> kinds, bool includeTrackPoints)
    {
        var (start, end) = DayRange(day);
        return _db.Run(connection =>
        {
            var buckets = new Dictionary<int, HashSet<int>>();
            CollectEventMinutesIntoBuckets(connection, "events", kinds, start, end, 60, buckets);
            if (includeTrackPoints)
            {
                CollectEventMinutesIntoBuckets(connection, "track_points", null, start, end, 60, buckets);
            }

            return SecondsByHour(buckets);
        });
    }

    public Dictionary<string, double> PerDayEventActiveSeconds(
        string startDay,
        string endDay,
        IReadOnlyList<string> kinds,
        bool includeTrackPoints)
    {
        var start = DayRange(startDay).Start;
        var end = DayRange(endDay).End;
        return _db.Run(connection =>
        {
            var buckets = new Dictionary<int, HashSet<int>>();
            CollectEventMinutesIntoBuckets(connection, "events", kinds, start, end, 1440, buckets);
            if (includeTrackPoints)
            {
                CollectEventMinutesIntoBuckets(connection, "track_points", null, start, end, 1440, buckets);
            }

            return DayBucketSeconds(buckets, start);
        });
    }

    public Dictionary<int, double> HourlyInteractionSeconds(string day, AppCategory? categoryFilter = null)
    {
        var (start, end) = DayRange(day);
        return _db.Run(connection =>
        {
            var buckets = new Dictionary<int, HashSet<int>>();
            CollectEventMinutesIntoBuckets(connection, "events", null, start, end, 60, buckets);
            CollectEventMinutesIntoBuckets(connection, "track_points", null, start, end, 60, buckets);
            CollectSessionMinutesIntoBuckets(connection, start, end, categoryFilter, 60, buckets);
            return SecondsByHour(buckets);
        });
    }

    public Dictionary<string, double> PerDayInteractionSeconds(string startDay, string endDay, AppCategory? categoryFilter = null)
    {
        var start = DayRange(startDay).Start;
        var end = DayRange(endDay).End;
        return _db.Run(connection =>
        {
            var buckets = new Dictionary<int, HashSet<int>>();
            CollectEventMinutesIntoBuckets(connection, "events", null, start, end, 1440, buckets);
            CollectEventMinutesIntoBuckets(connection, "track_points", null, start, end, 1440, buckets);
            CollectSessionMinutesIntoBuckets(connection, start, end, categoryFilter, 1440, buckets);
            return DayBucketSeconds(buckets, start);
        });
    }

    public IReadOnlyList<AppUsageSummary> AppUsage(double start, double end)
    {
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT app_name, bundle_id, category, SUM(duration)
                FROM app_usage
                WHERE start_ts BETWEEN $start AND $end
                GROUP BY bundle_id
                ORDER BY SUM(duration) DESC;
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            using var reader = command.ExecuteReader();
            var result = new List<AppUsageSummary>();
            while (reader.Read())
            {
                var bundleId = reader.GetString(1);
                var stored = reader.GetString(2);
                AppCategories.TryParse(stored, out var fallback);
                result.Add(new AppUsageSummary(
                    reader.GetString(0),
                    bundleId,
                    _categories.CategoryFor(bundleId) is var mapped && mapped != AppCategory.Other
                        ? mapped
                        : fallback,
                    reader.GetDouble(3)));
            }

            return result;
        });
    }

    public IReadOnlyList<KeyHeatItem> KeyHeat(string day, int limit = 30)
    {
        var (start, end) = DayRange(day);
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(characters, 'key:' || key_code) AS label, COUNT(*)
                FROM events
                WHERE ts BETWEEN $start AND $end AND kind = 'keyDown' AND is_auto_repeat = 0
                GROUP BY label
                ORDER BY COUNT(*) DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var result = new List<KeyHeatItem>();
            while (reader.Read())
            {
                result.Add(new KeyHeatItem(reader.GetString(0), reader.GetInt32(1)));
            }

            return result;
        });
    }

    public IReadOnlyList<(double X, double Y)> TrackPoints(string day)
    {
        var (start, end) = DayRange(day);
        return _db.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT x, y FROM track_points WHERE ts BETWEEN $start AND $end ORDER BY ts;";
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            using var reader = command.ExecuteReader();
            var result = new List<(double, double)>();
            while (reader.Read())
            {
                result.Add((reader.GetDouble(0), reader.GetDouble(1)));
            }

            return result;
        });
    }

    public double InteractionSeconds(string day)
    {
        var (start, end) = DayRange(day);
        return _db.Run(connection =>
        {
            var minutes = new HashSet<int>();
            CollectEventMinutes(connection, "events", start, end, minutes);
            CollectEventMinutes(connection, "track_points", start, end, minutes);
            CollectSessionMinutes(connection, start, end, minutes);
            return minutes.Count * 60d;
        });
    }

    public IReadOnlyCollection<int> InteractionMinutes(string day)
    {
        var (start, end) = DayRange(day);
        return _db.Run(connection =>
        {
            var minutes = new HashSet<int>();
            CollectEventMinutes(connection, "events", start, end, minutes);
            CollectEventMinutes(connection, "track_points", start, end, minutes);
            CollectSessionMinutes(connection, start, end, minutes);
            return minutes;
        });
    }

    public void PurgeExpiredData(int retentionDays)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-retentionDays);
        var cutoffTs = cutoff.ToUnixTimeSeconds();
        var cutoffDay = DayString(cutoff);
        _db.Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            DeleteByNumber(connection, transaction, "DELETE FROM events WHERE ts < $v;", cutoffTs);
            DeleteByNumber(connection, transaction, "DELETE FROM track_points WHERE ts < $v;", cutoffTs);
            DeleteByNumber(connection, transaction, "DELETE FROM app_usage WHERE start_ts < $v;", cutoffTs);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM daily_stats WHERE day < $v;";
                command.Parameters.AddWithValue("$v", cutoffDay);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        });
    }

    private bool MatchesCategory(string bundleId, AppCategory? categoryFilter) =>
        categoryFilter is null || _categories.CategoryFor(bundleId) == categoryFilter;

    private void CollectEventMinutesIntoBuckets(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string>? kinds,
        double start,
        double end,
        int bucketSize,
        Dictionary<int, HashSet<int>> buckets)
    {
        if (table is not ("events" or "track_points"))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "Unsupported table.");
        }

        if (kinds is { Count: 0 })
        {
            return;
        }

        using var command = connection.CreateCommand();
        var sql = $"SELECT DISTINCT CAST((ts - $origin) / 60 AS INTEGER) FROM {table} WHERE ts BETWEEN $start AND $end";
        if (kinds is { Count: > 0 })
        {
            sql += $" AND kind IN ({Placeholders(kinds.Count)})";
        }

        command.CommandText = sql;
        command.Parameters.AddWithValue("$origin", start);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        if (kinds is { Count: > 0 })
        {
            BindKinds(command, kinds);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var minute = reader.GetInt32(0);
            if (minute < 0)
            {
                continue;
            }

            var bucket = minute / bucketSize;
            if (!buckets.TryGetValue(bucket, out var set))
            {
                set = [];
                buckets[bucket] = set;
            }

            set.Add(minute);
        }
    }

    private void CollectSessionMinutesIntoBuckets(
        SqliteConnection connection,
        double start,
        double end,
        AppCategory? categoryFilter,
        int bucketSize,
        Dictionary<int, HashSet<int>> buckets)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bundle_id, start_ts, end_ts FROM app_usage WHERE end_ts >= $start AND start_ts < $end;";
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!MatchesCategory(reader.GetString(0), categoryFilter))
            {
                continue;
            }

            var sessionStart = Math.Max(reader.GetDouble(1), start);
            var sessionEnd = Math.Min(reader.GetDouble(2), end);
            var minute = (int)((sessionStart - start) / 60);
            var last = (int)((sessionEnd - start) / 60);
            while (minute <= last)
            {
                if (minute >= 0)
                {
                    var bucket = minute / bucketSize;
                    if (!buckets.TryGetValue(bucket, out var set))
                    {
                        set = [];
                        buckets[bucket] = set;
                    }

                    set.Add(minute);
                }

                minute++;
            }
        }
    }

    private static Dictionary<int, double> SecondsByHour(Dictionary<int, HashSet<int>> buckets)
    {
        var result = new Dictionary<int, double>();
        foreach (var (hour, minutes) in buckets)
        {
            if (hour is >= 0 and < 24)
            {
                result[hour] = minutes.Count * 60d;
            }
        }

        return result;
    }

    private static Dictionary<string, double> DayBucketSeconds(Dictionary<int, HashSet<int>> buckets, double rangeStart)
    {
        var origin = DateTimeOffset.FromUnixTimeSeconds((long)rangeStart).ToLocalTime().Date;
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (dayIndex, minutes) in buckets)
        {
            if (dayIndex < 0)
            {
                continue;
            }

            result[origin.AddDays(dayIndex).ToString("yyyy-MM-dd", DayCulture)] = minutes.Count * 60d;
        }

        return result;
    }

    private static IReadOnlyList<(int Minute, int Count)> ReadMinuteCounts(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new List<(int, int)>();
        while (reader.Read())
        {
            result.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return result;
    }

    private static Dictionary<string, int> ReadStringIntMap(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return result;
    }

    private static Dictionary<string, double> ReadStringDoubleMap(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetDouble(1);
        }

        return result;
    }

    private static void CollectEventMinutes(
        SqliteConnection connection,
        string table,
        double start,
        double end,
        HashSet<int> minutes)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT CAST((ts - $origin) / 60 AS INTEGER) FROM {table} WHERE ts BETWEEN $start AND $end;";
        command.Parameters.AddWithValue("$origin", start);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            minutes.Add(reader.GetInt32(0));
        }
    }

    private static void CollectSessionMinutes(
        SqliteConnection connection,
        double start,
        double end,
        HashSet<int> minutes)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT start_ts, end_ts FROM app_usage WHERE end_ts >= $start AND start_ts < $end;";
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sessionStart = Math.Max(reader.GetDouble(0), start);
            var sessionEnd = Math.Min(reader.GetDouble(1), end);
            var minute = (int)((sessionStart - start) / 60);
            var last = Math.Min((int)((sessionEnd - start) / 60), 1439);
            while (minute <= last)
            {
                minutes.Add(minute);
                minute++;
            }
        }
    }

    private static void DeleteByNumber(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        double value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    private static Dictionary<int, int> ReadIntMap(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new Dictionary<int, int>();
        while (reader.Read())
        {
            result[reader.GetInt32(0)] = reader.GetInt32(1);
        }

        return result;
    }

    private static string Placeholders(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(index => $"$k{index}"));

    private static void BindKinds(SqliteCommand command, IReadOnlyList<string> kinds)
    {
        for (var index = 0; index < kinds.Count; index++)
        {
            command.Parameters.AddWithValue($"$k{index}", kinds[index]);
        }
    }

    private static double ToUnix(DateTimeOffset value) => value.ToUnixTimeSeconds() + (value.Millisecond / 1000d);
}
