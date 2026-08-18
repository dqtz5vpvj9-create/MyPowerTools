using Microsoft.Data.Sqlite;

namespace InputMonitor.Core;

public sealed class MonitorDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public MonitorDatabase(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Migrate();
    }

    public void Execute(string sql)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    public void InTransaction(Action body)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                body();
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public SqliteCommand CreateCommand()
    {
        var command = _connection.CreateCommand();
        return command;
    }

    public void Run(Action<SqliteConnection> body)
    {
        lock (_gate)
        {
            body(_connection);
        }
    }

    public T Run<T>(Func<SqliteConnection, T> body)
    {
        lock (_gate)
        {
            return body(_connection);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }

    private void Migrate()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
                ts REAL NOT NULL,
                x REAL,
                y REAL,
                key_code INTEGER,
                characters TEXT,
                modifiers INTEGER DEFAULT 0,
                scroll_delta INTEGER DEFAULT 0,
                is_auto_repeat INTEGER DEFAULT 0
            );
            """);
        Execute("CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts);");
        Execute("CREATE INDEX IF NOT EXISTS idx_events_kind_ts ON events(kind, ts);");
        Execute("""
            CREATE TABLE IF NOT EXISTS track_points (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts REAL NOT NULL,
                x REAL NOT NULL,
                y REAL NOT NULL,
                move_delta REAL DEFAULT 0
            );
            """);
        Execute("CREATE INDEX IF NOT EXISTS idx_track_ts ON track_points(ts);");
        Execute("""
            CREATE TABLE IF NOT EXISTS app_usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                bundle_id TEXT NOT NULL,
                app_name TEXT NOT NULL,
                window_title TEXT,
                category TEXT NOT NULL,
                start_ts REAL NOT NULL,
                end_ts REAL NOT NULL,
                duration REAL NOT NULL
            );
            """);
        Execute("CREATE INDEX IF NOT EXISTS idx_app_usage_start ON app_usage(start_ts);");
        Execute("""
            CREATE TABLE IF NOT EXISTS daily_stats (
                day TEXT PRIMARY KEY,
                key_count INTEGER DEFAULT 0,
                click_count INTEGER DEFAULT 0,
                scroll_count INTEGER DEFAULT 0,
                key_duration_ms INTEGER DEFAULT 0,
                move_distance REAL DEFAULT 0,
                active_input_seconds REAL DEFAULT 0,
                active_app_seconds REAL DEFAULT 0,
                updated_at REAL
            );
            """);
    }
}
