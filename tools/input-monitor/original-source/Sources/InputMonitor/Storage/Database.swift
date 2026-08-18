import Foundation
import SQLite3

enum DatabaseError: Error {
    case openFailed(String)
    case execFailed(String)
    case prepareFailed(String)
    case bindFailed(String)
    case stepFailed(String)
}

/// SQLite3 连接与 schema 管理（所有读写均在内部串行队列执行）
final class Database {
    /// SQLITE_TRANSIENT：SQLite 内部拷贝字符串
    static let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

    private(set) var handle: OpaquePointer?
    let url: URL
    let queue = DispatchQueue(label: "com.local.inputmonitor.db")

    init(url: URL) throws {
        self.url = url
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        if sqlite3_open(url.path, &handle) != SQLITE_OK {
            let msg = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "unknown"
            throw DatabaseError.openFailed(msg)
        }
        try execute("PRAGMA journal_mode=WAL;")
        try execute("PRAGMA synchronous=NORMAL;")
        try migrate()
    }

    deinit {
        if let handle { sqlite3_close(handle) }
    }

    func execute(_ sql: String) throws {
        var err: UnsafeMutablePointer<Int8>?
        if sqlite3_exec(handle, sql, nil, nil, &err) != SQLITE_OK {
            let msg = err.map { String(cString: $0) } ?? "unknown"
            sqlite3_free(err)
            throw DatabaseError.execFailed("\(msg) | SQL: \(sql)")
        }
    }

    func prepare(_ sql: String) throws -> OpaquePointer {
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(handle, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else {
            let msg = String(cString: sqlite3_errmsg(handle))
            throw DatabaseError.prepareFailed("\(msg) | SQL: \(sql)")
        }
        return stmt
    }

    /// 在事务中执行批量操作
    func inTransaction(_ body: () throws -> Void) throws {
        try execute("BEGIN IMMEDIATE;")
        do {
            try body()
            try execute("COMMIT;")
        } catch {
            try? execute("ROLLBACK;")
            throw error
        }
    }

    private func migrate() throws {
        try execute("""
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
        """)
        try execute("CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts);")
        try execute("CREATE INDEX IF NOT EXISTS idx_events_kind_ts ON events(kind, ts);")

        try execute("""
        CREATE TABLE IF NOT EXISTS track_points (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts REAL NOT NULL,
            x REAL NOT NULL,
            y REAL NOT NULL,
            move_delta REAL DEFAULT 0
        );
        """)
        try execute("CREATE INDEX IF NOT EXISTS idx_track_ts ON track_points(ts);")

        try execute("""
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
        """)
        try execute("CREATE INDEX IF NOT EXISTS idx_app_usage_start ON app_usage(start_ts);")

        try execute("""
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
        """)
    }
}
