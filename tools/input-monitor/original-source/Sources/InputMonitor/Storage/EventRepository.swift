import Foundation
import SQLite3

/// 日聚合摘要（月/季/年看板与热力图数据源）
struct DaySummary {
    let day: String               // yyyy-MM-dd
    var keyCount: Int = 0
    var clickCount: Int = 0
    var scrollCount: Int = 0
    var keyDurationMs: Int64 = 0
    var moveDistance: Double = 0
    var activeInputSeconds: Double = 0
    var activeAppSeconds: Double = 0
}

/// 应用使用聚合
struct AppUsageSummary {
    let appName: String
    let bundleID: String
    let category: AppCategory
    var totalSeconds: Double
}

/// 按键热度项
struct KeyHeatItem {
    let label: String             // 字符或 keyCode 描述
    let count: Int
}

/// 存储读写接口（全部方法需在 Database.queue 之外调用，内部自行调度）
final class EventRepository {
    private let db: Database

    /// 日期格式化（本地时区，yyyy-MM-dd）
    static let dayFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        return f
    }()

    init(db: Database) {
        self.db = db
    }

    // MARK: - 写入

    func insertEvents(_ records: [InputEventRecord]) {
        guard !records.isEmpty else { return }
        db.queue.async {
            do {
                try self.db.inTransaction {
                    let stmt = try self.db.prepare("""
                    INSERT INTO events (kind, ts, x, y, key_code, characters, modifiers, scroll_delta, is_auto_repeat)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);
                    """)
                    defer { sqlite3_finalize(stmt) }
                    for r in records where r.kind != .mouseMoveSample {
                        self.bind(record: r, to: stmt)
                        if sqlite3_step(stmt) != SQLITE_DONE {
                            NSLog("[InputMonitor] insert event step failed")
                        }
                        sqlite3_reset(stmt)
                        sqlite3_clear_bindings(stmt)
                    }
                }
            } catch {
                NSLog("[InputMonitor] insert events failed: \(error.localizedDescription)")
            }
        }
    }

    func insertTrackPoints(_ records: [InputEventRecord]) {
        let points = records.filter { $0.kind == .mouseMoveSample }
        guard !points.isEmpty else { return }
        db.queue.async {
            do {
                try self.db.inTransaction {
                    let stmt = try self.db.prepare("""
                    INSERT INTO track_points (ts, x, y, move_delta) VALUES (?, ?, ?, ?);
                    """)
                    defer { sqlite3_finalize(stmt) }
                    for r in points {
                        sqlite3_bind_double(stmt, 1, r.wallTime.timeIntervalSince1970)
                        sqlite3_bind_double(stmt, 2, r.x ?? 0)
                        sqlite3_bind_double(stmt, 3, r.y ?? 0)
                        sqlite3_bind_double(stmt, 4, r.moveDelta)
                        if sqlite3_step(stmt) != SQLITE_DONE {
                            NSLog("[InputMonitor] insert track step failed")
                        }
                        sqlite3_reset(stmt)
                    }
                }
            } catch {
                NSLog("[InputMonitor] insert track points failed: \(error.localizedDescription)")
            }
        }
    }

    func insertAppSession(_ session: FrontAppSession) {
        guard let end = session.end else { return }
        let duration = end.timeIntervalSince(session.start)
        db.queue.async {
            do {
                let stmt = try self.db.prepare("""
                INSERT INTO app_usage (bundle_id, app_name, window_title, category, start_ts, end_ts, duration)
                VALUES (?, ?, ?, ?, ?, ?, ?);
                """)
                defer { sqlite3_finalize(stmt) }
                sqlite3_bind_text(stmt, 1, session.bundleID, -1, Database.SQLITE_TRANSIENT)
                sqlite3_bind_text(stmt, 2, session.appName, -1, Database.SQLITE_TRANSIENT)
                if let title = session.windowTitle {
                    sqlite3_bind_text(stmt, 3, title, -1, Database.SQLITE_TRANSIENT)
                } else {
                    sqlite3_bind_null(stmt, 3)
                }
                sqlite3_bind_text(stmt, 4, session.category.rawValue, -1, Database.SQLITE_TRANSIENT)
                sqlite3_bind_double(stmt, 5, session.start.timeIntervalSince1970)
                sqlite3_bind_double(stmt, 6, end.timeIntervalSince1970)
                sqlite3_bind_double(stmt, 7, duration)
                if sqlite3_step(stmt) != SQLITE_DONE {
                    NSLog("[InputMonitor] insert app session step failed")
                }
            } catch {
                NSLog("[InputMonitor] insert app session failed: \(error.localizedDescription)")
            }
        }
    }

    /// 累加当日聚合（upsert）
    func mergeDailyStats(day: String, delta: DaySummary) {
        db.queue.async {
            do {
                let stmt = try self.db.prepare("""
                INSERT INTO daily_stats (day, key_count, click_count, scroll_count, key_duration_ms, move_distance, active_input_seconds, active_app_seconds, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(day) DO UPDATE SET
                    key_count = key_count + excluded.key_count,
                    click_count = click_count + excluded.click_count,
                    scroll_count = scroll_count + excluded.scroll_count,
                    key_duration_ms = key_duration_ms + excluded.key_duration_ms,
                    move_distance = move_distance + excluded.move_distance,
                    active_input_seconds = active_input_seconds + excluded.active_input_seconds,
                    active_app_seconds = active_app_seconds + excluded.active_app_seconds,
                    updated_at = excluded.updated_at;
                """)
                defer { sqlite3_finalize(stmt) }
                sqlite3_bind_text(stmt, 1, day, -1, Database.SQLITE_TRANSIENT)
                sqlite3_bind_int64(stmt, 2, Int64(delta.keyCount))
                sqlite3_bind_int64(stmt, 3, Int64(delta.clickCount))
                sqlite3_bind_int64(stmt, 4, Int64(delta.scrollCount))
                sqlite3_bind_int64(stmt, 5, delta.keyDurationMs)
                sqlite3_bind_double(stmt, 6, delta.moveDistance)
                sqlite3_bind_double(stmt, 7, delta.activeInputSeconds)
                sqlite3_bind_double(stmt, 8, delta.activeAppSeconds)
                sqlite3_bind_double(stmt, 9, Date().timeIntervalSince1970)
                if sqlite3_step(stmt) != SQLITE_DONE {
                    NSLog("[InputMonitor] merge daily stats step failed")
                }
            } catch {
                NSLog("[InputMonitor] merge daily stats failed: \(error.localizedDescription)")
            }
        }
    }

    private func bind(record r: InputEventRecord, to stmt: OpaquePointer) {
        sqlite3_bind_text(stmt, 1, r.kind.rawValue, -1, Database.SQLITE_TRANSIENT)
        sqlite3_bind_double(stmt, 2, r.wallTime.timeIntervalSince1970)
        if let x = r.x { sqlite3_bind_double(stmt, 3, x) } else { sqlite3_bind_null(stmt, 3) }
        if let y = r.y { sqlite3_bind_double(stmt, 4, y) } else { sqlite3_bind_null(stmt, 4) }
        if let code = r.keyCode { sqlite3_bind_int64(stmt, 5, code) } else { sqlite3_bind_null(stmt, 5) }
        if let chars = r.characters { sqlite3_bind_text(stmt, 6, chars, -1, Database.SQLITE_TRANSIENT) } else { sqlite3_bind_null(stmt, 6) }
        sqlite3_bind_int64(stmt, 7, Int64(bitPattern: r.modifiers))
        sqlite3_bind_int64(stmt, 8, r.scrollDelta)
        sqlite3_bind_int(stmt, 9, r.isAutoRepeat ? 1 : 0)
    }

    // MARK: - 查询（同步返回，供 UI 在后台线程调用）

    /// 指定日期的聚合摘要（优先 daily_stats，无则从 events 现算兜底）
    func daySummary(for day: String) -> DaySummary {
        db.queue.sync {
            var summary = DaySummary(day: day)
            if let stmt = try? db.prepare("SELECT key_count, click_count, scroll_count, key_duration_ms, move_distance, active_input_seconds, active_app_seconds FROM daily_stats WHERE day = ?;") {
                defer { sqlite3_finalize(stmt) }
                sqlite3_bind_text(stmt, 1, day, -1, Database.SQLITE_TRANSIENT)
                if sqlite3_step(stmt) == SQLITE_ROW {
                    summary.keyCount = Int(sqlite3_column_int64(stmt, 0))
                    summary.clickCount = Int(sqlite3_column_int64(stmt, 1))
                    summary.scrollCount = Int(sqlite3_column_int64(stmt, 2))
                    summary.keyDurationMs = sqlite3_column_int64(stmt, 3)
                    summary.moveDistance = sqlite3_column_double(stmt, 4)
                    summary.activeInputSeconds = sqlite3_column_double(stmt, 5)
                    summary.activeAppSeconds = sqlite3_column_double(stmt, 6)
                }
            }
            return summary
        }
    }

    /// 日期范围内的日聚合（月/季/年热力）
    func daySummaries(from startDay: String, to endDay: String) -> [String: DaySummary] {
        db.queue.sync {
            var result: [String: DaySummary] = [:]
            guard let stmt = try? db.prepare("""
                SELECT day, key_count, click_count, scroll_count, key_duration_ms, move_distance, active_input_seconds, active_app_seconds
                FROM daily_stats WHERE day BETWEEN ? AND ?;
                """) else { return result }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_text(stmt, 1, startDay, -1, Database.SQLITE_TRANSIENT)
            sqlite3_bind_text(stmt, 2, endDay, -1, Database.SQLITE_TRANSIENT)
            while sqlite3_step(stmt) == SQLITE_ROW {
                let day = String(cString: sqlite3_column_text(stmt, 0))
                var s = DaySummary(day: day)
                s.keyCount = Int(sqlite3_column_int64(stmt, 1))
                s.clickCount = Int(sqlite3_column_int64(stmt, 2))
                s.scrollCount = Int(sqlite3_column_int64(stmt, 3))
                s.keyDurationMs = sqlite3_column_int64(stmt, 4)
                s.moveDistance = sqlite3_column_double(stmt, 5)
                s.activeInputSeconds = sqlite3_column_double(stmt, 6)
                s.activeAppSeconds = sqlite3_column_double(stmt, 7)
                result[day] = s
            }
            return result
        }
    }

    /// 某日按小时的操作数（kind 维度，分小时热力）
    func hourlyCounts(day: String, kinds: [String]) -> [Int: Int] {
        let (start, end) = Self.dayRange(day: day)
        return db.queue.sync {
            var result: [Int: Int] = [:]
            let placeholders = kinds.map { _ in "?" }.joined(separator: ",")
            guard let stmt = try? db.prepare("""
                SELECT CAST(strftime('%H', datetime(ts, 'unixepoch', 'localtime')) AS INTEGER) AS hour, COUNT(*)
                FROM events
                WHERE ts BETWEEN ? AND ? AND kind IN (\(placeholders)) AND is_auto_repeat = 0
                GROUP BY hour;
                """) else { return result }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_double(stmt, 1, start)
            sqlite3_bind_double(stmt, 2, end)
            for (i, kind) in kinds.enumerated() {
                sqlite3_bind_text(stmt, Int32(i + 3), kind, -1, Database.SQLITE_TRANSIENT)
            }
            while sqlite3_step(stmt) == SQLITE_ROW {
                result[Int(sqlite3_column_int(stmt, 0))] = Int(sqlite3_column_int64(stmt, 1))
            }
            return result
        }
    }

    /// 某日按小时的应用使用秒数（按覆盖表重映射分类，可选类型筛选）
    func hourlyAppSeconds(day: String, categoryFilter: AppCategory? = nil) -> [Int: Double] {
        let (start, end) = Self.dayRange(day: day)
        return db.queue.sync {
            var result: [Int: Double] = [:]
            guard let stmt = try? db.prepare("""
                SELECT bundle_id,
                       CAST(strftime('%H', datetime(start_ts, 'unixepoch', 'localtime')) AS INTEGER) AS hour,
                       SUM(duration)
                FROM app_usage
                WHERE start_ts BETWEEN ? AND ?
                GROUP BY bundle_id, hour;
                """) else { return result }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_double(stmt, 1, start)
            sqlite3_bind_double(stmt, 2, end)
            while sqlite3_step(stmt) == SQLITE_ROW {
                let bundleID = String(cString: sqlite3_column_text(stmt, 0))
                if let categoryFilter, AppCategoryMap.shared.category(for: bundleID) != categoryFilter { continue }
                result[Int(sqlite3_column_int(stmt, 1)), default: 0] += sqlite3_column_double(stmt, 2)
            }
            return result
        }
    }

    /// 范围按天的应用使用秒数（按覆盖表重映射分类，可选类型筛选）
    func perDayAppSeconds(from startDay: String, to endDay: String, categoryFilter: AppCategory? = nil) -> [String: Double] {
        let start = Self.dayRange(day: startDay).0
        let end = Self.dayRange(day: endDay).1
        return db.queue.sync {
            var result: [String: Double] = [:]
            guard let stmt = try? db.prepare("""
                SELECT bundle_id,
                       strftime('%Y-%m-%d', datetime(start_ts, 'unixepoch', 'localtime')) AS day,
                       SUM(duration)
                FROM app_usage
                WHERE start_ts BETWEEN ? AND ?
                GROUP BY bundle_id, day;
                """) else { return result }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_double(stmt, 1, start)
            sqlite3_bind_double(stmt, 2, end)
            while sqlite3_step(stmt) == SQLITE_ROW {
                let bundleID = String(cString: sqlite3_column_text(stmt, 0))
                if let categoryFilter, AppCategoryMap.shared.category(for: bundleID) != categoryFilter { continue }
                if let cstr = sqlite3_column_text(stmt, 1) {
                    result[String(cString: cstr), default: 0] += sqlite3_column_double(stmt, 2)
                }
            }
            return result
        }
    }

    /// 日期范围内按应用聚合的使用时长
    func appUsage(from start: TimeInterval, to end: TimeInterval) -> [AppUsageSummary] {
        db.queue.sync {
            // GROUP BY 已保证 bundle_id 唯一，直接按 SQL 的 DESC 顺序收集，勿经字典中转（会丢序）
            var result: [AppUsageSummary] = []
            guard let stmt = try? db.prepare("""
                SELECT app_name, bundle_id, category, SUM(duration)
                FROM app_usage
                WHERE start_ts BETWEEN ? AND ?
                GROUP BY bundle_id
                ORDER BY SUM(duration) DESC;
                """) else { return [] }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_double(stmt, 1, start)
            sqlite3_bind_double(stmt, 2, end)
            while sqlite3_step(stmt) == SQLITE_ROW {
                let appName = String(cString: sqlite3_column_text(stmt, 0))
                let bundleID = String(cString: sqlite3_column_text(stmt, 1))
                let category = AppCategory(rawValue: String(cString: sqlite3_column_text(stmt, 2))) ?? .other
                let seconds = sqlite3_column_double(stmt, 3)
                result.append(AppUsageSummary(appName: appName, bundleID: bundleID, category: category, totalSeconds: seconds))
            }
            return result
        }
    }

    /// 某日按键热度（按字符/键码计数，倒序）
    func keyHeat(day: String, limit: Int = 30) -> [KeyHeatItem] {
        let (start, end) = Self.dayRange(day: day)
        return db.queue.sync {
            var result: [KeyHeatItem] = []
            guard let stmt = try? db.prepare("""
                SELECT COALESCE(characters, 'key:' || key_code) AS label, COUNT(*)
                FROM events
                WHERE ts BETWEEN ? AND ? AND kind = 'keyDown' AND is_auto_repeat = 0
                GROUP BY label
                ORDER BY COUNT(*) DESC
                LIMIT ?;
                """) else { return result }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_double(stmt, 1, start)
            sqlite3_bind_double(stmt, 2, end)
            sqlite3_bind_int(stmt, 3, Int32(limit))
            while sqlite3_step(stmt) == SQLITE_ROW {
                let label = String(cString: sqlite3_column_text(stmt, 0))
                result.append(KeyHeatItem(label: label, count: Int(sqlite3_column_int64(stmt, 1))))
            }
            return result
        }
    }

    /// 某日鼠标轨迹采样点
    func trackPoints(day: String) -> [(x: Double, y: Double)] {
        let (start, end) = Self.dayRange(day: day)
        return db.queue.sync {
            var result: [(Double, Double)] = []
            guard let stmt = try? db.prepare("""
                SELECT x, y FROM track_points WHERE ts BETWEEN ? AND ? ORDER BY ts;
                """) else { return result }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_double(stmt, 1, start)
            sqlite3_bind_double(stmt, 2, end)
            while sqlite3_step(stmt) == SQLITE_ROW {
                result.append((sqlite3_column_double(stmt, 0), sqlite3_column_double(stmt, 1)))
            }
            return result
        }
    }

    /// 某日内每分钟的操作数序列（严格口径：指定 kinds 且剔除自动重复；返回 [(分钟偏移, 计数)]）
    func perMinuteCounts(day: String, kinds: [String]) -> [(minute: Int, count: Int)] {
        let (start, end) = Self.dayRange(day: day)
        return db.queue.sync {
            var result: [(Int, Int)] = []
            let placeholders = kinds.map { _ in "?" }.joined(separator: ",")
            guard let stmt = try? db.prepare("""
                SELECT (CAST(strftime('%H', datetime(ts, 'unixepoch', 'localtime')) AS INTEGER) * 60
                        + CAST(strftime('%M', datetime(ts, 'unixepoch', 'localtime')) AS INTEGER)) AS minute,
                       COUNT(*)
                FROM events
                WHERE ts BETWEEN ? AND ? AND kind IN (\(placeholders)) AND is_auto_repeat = 0
                GROUP BY minute
                ORDER BY minute;
                """) else { return result }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_double(stmt, 1, start)
            sqlite3_bind_double(stmt, 2, end)
            for (i, kind) in kinds.enumerated() {
                sqlite3_bind_text(stmt, Int32(i + 3), kind, -1, Database.SQLITE_TRANSIENT)
            }
            while sqlite3_step(stmt) == SQLITE_ROW {
                result.append((Int(sqlite3_column_int(stmt, 0)), Int(sqlite3_column_int64(stmt, 1))))
            }
            return result
        }
    }

    /// 某日内每分钟的窗口切换次数（应用维度的"操作"：每次前台会话开始记一次）
    func perMinuteSessionStarts(day: String) -> [(minute: Int, count: Int)] {
        let (start, end) = Self.dayRange(day: day)
        return db.queue.sync {
            var result: [(Int, Int)] = []
            guard let stmt = try? db.prepare("""
                SELECT (CAST(strftime('%H', datetime(start_ts, 'unixepoch', 'localtime')) AS INTEGER) * 60
                        + CAST(strftime('%M', datetime(start_ts, 'unixepoch', 'localtime')) AS INTEGER)) AS minute,
                       COUNT(*)
                FROM app_usage
                WHERE start_ts BETWEEN ? AND ?
                GROUP BY minute
                ORDER BY minute;
                """) else { return result }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_double(stmt, 1, start)
            sqlite3_bind_double(stmt, 2, end)
            while sqlite3_step(stmt) == SQLITE_ROW {
                result.append((Int(sqlite3_column_int(stmt, 0)), Int(sqlite3_column_int64(stmt, 1))))
            }
            return result
        }
    }

    /// 日期范围内按天的操作数（严格口径；月/季/年操作频次曲线）
    func perDayOperationCounts(from startDay: String, to endDay: String, kinds: [String]) -> [String: Int] {
        let start = Self.dayRange(day: startDay).0
        let end = Self.dayRange(day: endDay).1
        return db.queue.sync {
            var result: [String: Int] = [:]
            let placeholders = kinds.map { _ in "?" }.joined(separator: ",")
            guard let stmt = try? db.prepare("""
                SELECT strftime('%Y-%m-%d', datetime(ts, 'unixepoch', 'localtime')) AS day, COUNT(*)
                FROM events
                WHERE ts BETWEEN ? AND ? AND kind IN (\(placeholders)) AND is_auto_repeat = 0
                GROUP BY day;
                """) else { return result }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_double(stmt, 1, start)
            sqlite3_bind_double(stmt, 2, end)
            for (i, kind) in kinds.enumerated() {
                sqlite3_bind_text(stmt, Int32(i + 3), kind, -1, Database.SQLITE_TRANSIENT)
            }
            while sqlite3_step(stmt) == SQLITE_ROW {
                if let cstr = sqlite3_column_text(stmt, 0) {
                    result[String(cString: cstr)] = Int(sqlite3_column_int64(stmt, 1))
                }
            }
            return result
        }
    }

    /// 日期范围内按天的窗口切换次数（应用维度）
    func perDaySessionStarts(from startDay: String, to endDay: String) -> [String: Int] {
        let start = Self.dayRange(day: startDay).0
        let end = Self.dayRange(day: endDay).1
        return db.queue.sync {
            var result: [String: Int] = [:]
            guard let stmt = try? db.prepare("""
                SELECT strftime('%Y-%m-%d', datetime(start_ts, 'unixepoch', 'localtime')) AS day, COUNT(*)
                FROM app_usage
                WHERE start_ts BETWEEN ? AND ?
                GROUP BY day;
                """) else { return result }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_double(stmt, 1, start)
            sqlite3_bind_double(stmt, 2, end)
            while sqlite3_step(stmt) == SQLITE_ROW {
                if let cstr = sqlite3_column_text(stmt, 0) {
                    result[String(cString: cstr)] = Int(sqlite3_column_int64(stmt, 1))
                }
            }
            return result
        }
    }

    /// 按天聚合的窗口活动秒数（来自 app_usage 真实会话时长；会话按 start_ts 归日）
    func appSecondsByDay(from startDay: String, to endDay: String) -> [String: Double] {
        let start = Self.dayRange(day: startDay).0
        let end = Self.dayRange(day: endDay).1
        return db.queue.sync {
            var result: [String: Double] = [:]
            guard let stmt = try? db.prepare("""
                SELECT strftime('%Y-%m-%d', datetime(start_ts, 'unixepoch', 'localtime')) AS day, SUM(duration)
                FROM app_usage
                WHERE start_ts BETWEEN ? AND ?
                GROUP BY day;
                """) else { return result }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_double(stmt, 1, start)
            sqlite3_bind_double(stmt, 2, end)
            while sqlite3_step(stmt) == SQLITE_ROW {
                if let cstr = sqlite3_column_text(stmt, 0) {
                    result[String(cString: cstr)] = sqlite3_column_double(stmt, 1)
                }
            }
            return result
        }
    }

    /// 某日互动总秒数（分钟粒度去重：键鼠事件 ∪ 轨迹采样 ∪ 应用会话，有活动的分钟计 60s）
    func interactionSeconds(day: String) -> Double {
        let (start, end) = Self.dayRange(day: day)
        return db.queue.sync {
            var minutes = Set<Int>()

            // 事件与轨迹采样覆盖的分钟
            for table in ["events", "track_points"] {
                guard let stmt = try? db.prepare("""
                    SELECT DISTINCT CAST((ts - ?) / 60 AS INTEGER) FROM \(table) WHERE ts BETWEEN ? AND ?;
                    """) else { continue }
                sqlite3_bind_double(stmt, 1, start)
                sqlite3_bind_double(stmt, 2, start)
                sqlite3_bind_double(stmt, 3, end)
                while sqlite3_step(stmt) == SQLITE_ROW {
                    minutes.insert(Int(sqlite3_column_int(stmt, 0)))
                }
                sqlite3_finalize(stmt)
            }

            // 应用会话覆盖的分钟（与当日求交后按分钟展开）
            if let stmt = try? db.prepare("""
                SELECT start_ts, end_ts FROM app_usage WHERE end_ts >= ? AND start_ts < ?;
                """) {
                sqlite3_bind_double(stmt, 1, start)
                sqlite3_bind_double(stmt, 2, end)
                while sqlite3_step(stmt) == SQLITE_ROW {
                    let s = max(sqlite3_column_double(stmt, 0), start)
                    let e = min(sqlite3_column_double(stmt, 1), end)
                    var m = Int((s - start) / 60)
                    let last = min(Int((e - start) / 60), 1439)
                    while m <= last {
                        minutes.insert(m)
                        m += 1
                    }
                }
                sqlite3_finalize(stmt)
            }

            return Double(minutes.count * 60)
        }
    }

    // MARK: - 活动时长（分钟桶去重）

    /// 内部：把事件表在 [start,end) 内的 DISTINCT 分钟并入桶（bucketSize=60 分小时 / 1440 按天；须在 db.queue 上调用）
    private func collectEventMinutes(table: String, kinds: [String]?, start: TimeInterval, end: TimeInterval, bucketSize: Int, into buckets: inout [Int: Set<Int>]) {
        var sql = "SELECT DISTINCT CAST((ts - ?) / 60 AS INTEGER) FROM \(table) WHERE ts BETWEEN ? AND ?"
        if let kinds {
            sql += " AND kind IN (\(kinds.map { _ in "?" }.joined(separator: ",")))"
        }
        guard let stmt = try? db.prepare(sql) else { return }
        sqlite3_bind_double(stmt, 1, start)
        sqlite3_bind_double(stmt, 2, start)
        sqlite3_bind_double(stmt, 3, end)
        if let kinds {
            for (i, kind) in kinds.enumerated() {
                sqlite3_bind_text(stmt, Int32(i + 4), kind, -1, Database.SQLITE_TRANSIENT)
            }
        }
        while sqlite3_step(stmt) == SQLITE_ROW {
            let m = Int(sqlite3_column_int(stmt, 0))
            buckets[m / bucketSize, default: []].insert(m)
        }
        sqlite3_finalize(stmt)
    }

    /// 内部：把 app_usage 会话覆盖的分钟并入桶（按覆盖表重映射类型后可筛选；须在 db.queue 上调用）
    private func collectSessionMinutes(start: TimeInterval, end: TimeInterval, categoryFilter: AppCategory?, bucketSize: Int, into buckets: inout [Int: Set<Int>]) {
        guard let stmt = try? db.prepare("""
            SELECT bundle_id, start_ts, end_ts FROM app_usage WHERE end_ts >= ? AND start_ts < ?;
            """) else { return }
        sqlite3_bind_double(stmt, 1, start)
        sqlite3_bind_double(stmt, 2, end)
        while sqlite3_step(stmt) == SQLITE_ROW {
            let bundleID = String(cString: sqlite3_column_text(stmt, 0))
            if let categoryFilter, AppCategoryMap.shared.category(for: bundleID) != categoryFilter { continue }
            let s = max(sqlite3_column_double(stmt, 1), start)
            let e = min(sqlite3_column_double(stmt, 2), end)
            var m = Int((s - start) / 60)
            let last = Int((e - start) / 60)
            while m <= last {
                buckets[m / bucketSize, default: []].insert(m)
                m += 1
            }
        }
        sqlite3_finalize(stmt)
    }

    /// 某日分小时互动秒数（全部来源并集：键鼠事件 ∪ 轨迹 ∪ 应用会话；会话可按类型筛选）
    func hourlyInteractionSeconds(day: String, categoryFilter: AppCategory? = nil) -> [Int: Double] {
        let (start, end) = Self.dayRange(day: day)
        return db.queue.sync {
            var buckets: [Int: Set<Int>] = [:]
            collectEventMinutes(table: "events", kinds: nil, start: start, end: end, bucketSize: 60, into: &buckets)
            collectEventMinutes(table: "track_points", kinds: nil, start: start, end: end, bucketSize: 60, into: &buckets)
            collectSessionMinutes(start: start, end: end, categoryFilter: categoryFilter, bucketSize: 60, into: &buckets)
            return buckets.mapValues { Double($0.count * 60) }
        }
    }

    /// 范围按天互动秒数（全部来源并集）
    func perDayInteractionSeconds(from startDay: String, to endDay: String, categoryFilter: AppCategory? = nil) -> [String: Double] {
        let start = Self.dayRange(day: startDay).0
        let end = Self.dayRange(day: endDay).1
        return db.queue.sync {
            var buckets: [Int: Set<Int>] = [:]
            collectEventMinutes(table: "events", kinds: nil, start: start, end: end, bucketSize: 1440, into: &buckets)
            collectEventMinutes(table: "track_points", kinds: nil, start: start, end: end, bucketSize: 1440, into: &buckets)
            collectSessionMinutes(start: start, end: end, categoryFilter: categoryFilter, bucketSize: 1440, into: &buckets)
            return dayBucketSeconds(buckets: buckets, rangeStart: start)
        }
    }

    /// 某日分小时"输入事件"活动秒数（键盘/鼠标维度的活动时长：事件分钟桶，鼠标含轨迹点）
    func hourlyEventActiveSeconds(day: String, kinds: [String], includeTrackPoints: Bool) -> [Int: Double] {
        let (start, end) = Self.dayRange(day: day)
        return db.queue.sync {
            var buckets: [Int: Set<Int>] = [:]
            collectEventMinutes(table: "events", kinds: kinds, start: start, end: end, bucketSize: 60, into: &buckets)
            if includeTrackPoints {
                collectEventMinutes(table: "track_points", kinds: nil, start: start, end: end, bucketSize: 60, into: &buckets)
            }
            return buckets.mapValues { Double($0.count * 60) }
        }
    }

    /// 范围按天"输入事件"活动秒数
    func perDayEventActiveSeconds(from startDay: String, to endDay: String, kinds: [String], includeTrackPoints: Bool) -> [String: Double] {
        let start = Self.dayRange(day: startDay).0
        let end = Self.dayRange(day: endDay).1
        return db.queue.sync {
            var buckets: [Int: Set<Int>] = [:]
            collectEventMinutes(table: "events", kinds: kinds, start: start, end: end, bucketSize: 1440, into: &buckets)
            if includeTrackPoints {
                collectEventMinutes(table: "track_points", kinds: nil, start: start, end: end, bucketSize: 1440, into: &buckets)
            }
            return dayBucketSeconds(buckets: buckets, rangeStart: start)
        }
    }

    /// 桶索引（分钟/1440）→ 日期字符串 的秒数映射
    private func dayBucketSeconds(buckets: [Int: Set<Int>], rangeStart: TimeInterval) -> [String: Double] {
        var result: [String: Double] = [:]
        for (dayIndex, minutes) in buckets {
            if let date = Calendar.current.date(byAdding: .day, value: dayIndex, to: Date(timeIntervalSince1970: rangeStart)) {
                result[Self.dayString(for: date)] = Double(minutes.count * 60)
            }
        }
        return result
    }

    /// 屏障同步：等待数据库队列上所有待执行写入完成（退出前调用）
    func barrierSync() {
        db.queue.sync {}
    }

    // MARK: - 数据清理

    /// 清理早于保留周期的历史数据（启动时与修改设置后调用；异步在 db 队列执行）
    /// events/track_points/app_usage 按时间戳截断，daily_stats 按天字符串截断
    func purgeExpiredData(retentionDays: Int) {
        let cutoff = Date().addingTimeInterval(-TimeInterval(retentionDays) * 86400)
        let cutoffTs = cutoff.timeIntervalSince1970
        let cutoffDay = Self.dayString(for: cutoff)
        db.queue.async {
            do {
                try self.db.inTransaction {
                    try self.deleteRows("DELETE FROM events WHERE ts < ?;", cutoffTs)
                    try self.deleteRows("DELETE FROM track_points WHERE ts < ?;", cutoffTs)
                    try self.deleteRows("DELETE FROM app_usage WHERE start_ts < ?;", cutoffTs)
                    try self.deleteRows("DELETE FROM daily_stats WHERE day < ?;", cutoffDay)
                }
                // 清理后收缩 WAL 文件
                try? self.db.execute("PRAGMA wal_checkpoint(TRUNCATE);")
                FileLogger.log("purge done: retention=\(retentionDays)d cutoff=\(cutoffDay)")
            } catch {
                FileLogger.log("purge FAILED: \(error.localizedDescription)")
            }
        }
    }

    private func deleteRows(_ sql: String, _ value: Double) throws {
        let stmt = try db.prepare(sql)
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_double(stmt, 1, value)
        guard sqlite3_step(stmt) == SQLITE_DONE else { throw DatabaseError.stepFailed(sql) }
    }

    private func deleteRows(_ sql: String, _ text: String) throws {
        let stmt = try db.prepare(sql)
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_text(stmt, 1, text, -1, Database.SQLITE_TRANSIENT)
        guard sqlite3_step(stmt) == SQLITE_DONE else { throw DatabaseError.stepFailed(sql) }
    }

    // MARK: - 工具

    /// 返回某日的 [当天00:00, 次日00:00) unix 时间范围
    static func dayRange(day: String) -> (TimeInterval, TimeInterval) {
        guard let date = dayFormatter.date(from: day) else {
            let now = Date().timeIntervalSince1970
            return (now, now)
        }
        let start = date.timeIntervalSince1970
        return (start, start + 86400)
    }

    static func dayString(for date: Date = Date()) -> String {
        dayFormatter.string(from: date)
    }
}
