import Foundation

/// 指标聚合器：在事件串行队列中被逐个喂入事件，维护当日增量与累计计数
/// - keyDown/keyUp 配对计算按住时长（剔除 ARepeat 重置）
/// - 事件间隔 ≤60s 视为连续活动，累加活动时间
/// - 跨天自动轮换计数器
final class MetricsAggregator {
    /// 连续活动允许的最大事件间隔（秒）
    var activeGapThresholdSeconds: TimeInterval = 60

    // MARK: - 当日累计（供 UI 实时展示）
    private(set) var day: String
    private(set) var keyCount = 0
    private(set) var clickCount = 0
    private(set) var scrollCount = 0
    private(set) var moveDistance = 0.0        // px
    private(set) var keyDurationMs: Int64 = 0
    private(set) var activeInputSeconds = 0.0
    private(set) var activeAppSeconds = 0.0

    // MARK: - 未落库增量
    private var deltaKeyCount = 0
    private var deltaClickCount = 0
    private var deltaScrollCount = 0
    private var deltaMoveDistance = 0.0
    private var deltaKeyDurationMs: Int64 = 0
    private var deltaActiveInputSeconds = 0.0
    private var deltaActiveAppSeconds = 0.0

    // MARK: - 配对与活动状态
    private var keyDownTimestamps: [Int64: UInt64] = [:]
    private var lastEventWallTime: Date?

    /// 互动分钟桶（当日）：任意来源活动（键鼠事件 ∪ 窗口心跳）按分钟去重
    private var activeMinutes = Set<Int>()

    init(day: String = EventRepository.dayString()) {
        self.day = day
    }

    /// 喂入一条事件（必须在同一串行队列调用）
    func process(_ record: InputEventRecord) {
        rotateIfNeeded(for: record.wallTime)
        accumulateActiveTime(at: record.wallTime)
        activeMinutes.insert(Self.minuteIndex(of: record.wallTime))

        switch record.kind {
        case .keyDown:
            guard !record.isAutoRepeat else { return }
            keyCount += 1
            deltaKeyCount += 1
            if let code = record.keyCode {
                keyDownTimestamps[code] = record.timestampNs
            }

        case .keyUp:
            guard let code = record.keyCode,
                  let downTs = keyDownTimestamps.removeValue(forKey: code) else { return }
            let durationNs = record.timestampNs &- downTs
            let ms = Int64(durationNs / 1_000_000)
            // 异常长按（>10min）多为系统休眠干扰，丢弃
            guard ms < 600_000 else { return }
            keyDurationMs += ms
            deltaKeyDurationMs += ms

        case .leftClick, .rightClick:
            clickCount += 1
            deltaClickCount += 1

        case .scroll:
            let lines = max(1, abs(record.scrollDelta))
            scrollCount += Int(lines)
            deltaScrollCount += Int(lines)

        case .mouseMoveSample:
            moveDistance += record.moveDelta
            deltaMoveDistance += record.moveDelta

        case .flagsChanged:
            break
        }
    }

    /// 应用心跳：累加窗口活动时间（heartbeatInterval 秒）；未锁屏时并入互动分钟桶
    func processAppHeartbeat(interval: TimeInterval) {
        rotateIfNeeded(for: Date())
        activeAppSeconds += interval
        deltaActiveAppSeconds += interval
        if !ScreenState.isLocked {
            activeMinutes.insert(Self.minuteIndex(of: Date()))
        }
    }

    /// 互动总秒数（分钟粒度，键鼠与窗口活动去重并集）
    var interactionSeconds: Double {
        Double(activeMinutes.count) * 60
    }

    /// 取走未落库增量（用于 mergeDailyStats）
    func drainDelta() -> (day: String, delta: DaySummary) {
        var delta = DaySummary(day: day)
        delta.keyCount = deltaKeyCount
        delta.clickCount = deltaClickCount
        delta.scrollCount = deltaScrollCount
        delta.keyDurationMs = deltaKeyDurationMs
        delta.moveDistance = deltaMoveDistance
        delta.activeInputSeconds = deltaActiveInputSeconds
        delta.activeAppSeconds = deltaActiveAppSeconds
        deltaKeyCount = 0
        deltaClickCount = 0
        deltaScrollCount = 0
        deltaKeyDurationMs = 0
        deltaMoveDistance = 0
        deltaActiveInputSeconds = 0
        deltaActiveAppSeconds = 0
        return (day, delta)
    }

    // MARK: - 快照（必须在事件串行队列上调用）

    struct Snapshot {
        let day: String
        let keyCount: Int
        let clickCount: Int
        let scrollCount: Int
        let moveDistance: Double
        let keyDurationMs: Int64
        let activeInputSeconds: Double
        let activeAppSeconds: Double
        let interactionSeconds: Double
    }

    func snapshot() -> Snapshot {
        Snapshot(
            day: day,
            keyCount: keyCount,
            clickCount: clickCount,
            scrollCount: scrollCount,
            moveDistance: moveDistance,
            keyDurationMs: keyDurationMs,
            activeInputSeconds: activeInputSeconds,
            activeAppSeconds: activeAppSeconds,
            interactionSeconds: interactionSeconds
        )
    }

    // MARK: - 私有

    /// 活动时间按墙上时间（wallTime）计算：对任何事件来源/时间基都一致可靠
    private func accumulateActiveTime(at wallTime: Date) {
        if let last = lastEventWallTime {
            let gap = wallTime.timeIntervalSince(last)
            if gap >= 0, gap <= activeGapThresholdSeconds {
                activeInputSeconds += gap
                deltaActiveInputSeconds += gap
            }
        }
        lastEventWallTime = wallTime
    }

    private func rotateIfNeeded(for date: Date) {
        let currentDay = EventRepository.dayString(for: date)
        guard currentDay != day else { return }
        // 跨天：重置计数器（增量由外部在跨天前 flush）
        day = currentDay
        keyCount = 0
        clickCount = 0
        scrollCount = 0
        moveDistance = 0
        keyDurationMs = 0
        activeInputSeconds = 0
        activeAppSeconds = 0
        keyDownTimestamps.removeAll()
        lastEventWallTime = nil
        activeMinutes.removeAll()
    }

    /// 当日分钟索引（0...1439）
    static func minuteIndex(of date: Date) -> Int {
        let comps = Calendar.current.dateComponents([.hour, .minute], from: date)
        return (comps.hour ?? 0) * 60 + (comps.minute ?? 0)
    }
}
