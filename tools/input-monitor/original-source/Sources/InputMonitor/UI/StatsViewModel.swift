import Foundation

/// 统计面板 ViewModel：日/月/季/年多粒度 + 键盘/鼠标/应用三维度 + 应用类型筛选
final class StatsViewModel: ObservableObject {
    enum Granularity: String, CaseIterable, Identifiable {
        case day = "日"
        case month = "月"
        case quarter = "季"
        case year = "年"
        var id: String { rawValue }
    }

    enum Dimension: String, CaseIterable, Identifiable {
        case all = "所有"
        case keyboard = "键盘"
        case mouse = "鼠标"
        case app = "应用"
        var id: String { rawValue }
    }

    @Published var granularity: Granularity = .day { didSet { reload() } }
    @Published var dimension: Dimension = .keyboard { didSet { reload() } }
    @Published var categoryFilter: AppCategory? { didSet { reload() } }
    /// 日粒度下查看的具体日期（默认今天）
    @Published var selectedDate: Date = Date() { didSet { reload() } }

    @Published private(set) var daySummaries: [String: DaySummary] = [:]
    @Published private(set) var appSecondsByDay: [String: Double] = [:]
    /// 周网格热力（应用维度，带类型筛选）
    @Published private(set) var perDayFilteredAppSeconds: [String: Double] = [:]
    /// 周网格热力（所有维度，互动秒数并集）
    @Published private(set) var perDayInteraction: [String: Double] = [:]
    @Published private(set) var recent7Hourly: [(day: String, hourly: [Int: Double])] = []
    @Published private(set) var keyHeat: [KeyHeatItem] = []
    @Published private(set) var trackPoints: [(x: Double, y: Double)] = []
    @Published private(set) var appUsage: [AppUsageSummary] = []
    /// 操作频次（日粒度）：分钟序列
    @Published private(set) var perMinute: [(minute: Int, count: Int)] = []
    /// 操作频次（月/季/年粒度）：按天序列
    @Published private(set) var perDayFrequency: [(day: String, count: Int)] = []
    /// 活动时长（日粒度）：0...23 每小时的活动秒数（随维度/类型筛选）
    @Published private(set) var hourlyActivity: [(hour: Int, seconds: Double)] = []
    /// 活动时长（月/季/年粒度）：按天序列
    @Published private(set) var perDayActivity: [(day: String, seconds: Double)] = []
    /// 选中日期互动总秒数（DB 分钟桶去重；仅日粒度有值，今天由 UI 侧用实时值覆盖）
    @Published private(set) var selectedDayInteraction: Double = 0

    let repository: EventRepository

    /// 应用分类覆盖变更观察令牌（变更时自动 reload 让配置立即生效）
    private var categoryObserver: NSObjectProtocol?

    private var calendar: Calendar {
        var c = Calendar(identifier: .gregorian)
        c.firstWeekday = 2 // 周一为一周之始
        return c
    }

    var today: String { EventRepository.dayString() }

    /// 选中日期的天字符串 / 是否就是今天
    var selectedDay: String { EventRepository.dayString(for: selectedDate) }
    var isSelectedToday: Bool { selectedDay == today }

    init(repository: EventRepository) {
        self.repository = repository
        categoryObserver = NotificationCenter.default.addObserver(
            forName: AppCategoryMap.didChangeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.reload()
        }
        reload()
    }

    deinit {
        stopAutoRefresh()
        if let categoryObserver {
            NotificationCenter.default.removeObserver(categoryObserver)
        }
    }

    // MARK: - 自动刷新

    /// 面板可见期间的自动刷新定时器（30s，与 daily_stats 30s drain 节奏对齐）
    private var autoRefreshTimer: Timer?

    /// 启动自动刷新（幂等；面板可见时调用）
    func startAutoRefresh() {
        guard autoRefreshTimer == nil else { return }
        let timer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            self?.reload()
        }
        RunLoop.main.add(timer, forMode: .common)
        autoRefreshTimer = timer
    }

    /// 停止自动刷新（面板关闭/切走时调用，避免后台空跑查询）
    func stopAutoRefresh() {
        autoRefreshTimer?.invalidate()
        autoRefreshTimer = nil
    }

    // MARK: - 维度取值

    func heatValue(for summary: DaySummary?) -> Double {
        guard let s = summary else { return 0 }
        switch dimension {
        case .keyboard: return Double(s.keyCount)
        case .mouse:    return Double(s.clickCount + s.scrollCount)
        case .app, .all: return 0 // 应用/所有维度走专用数据源
        }
    }

    /// 周网格热力值字典（day -> value）
    var heatDayValues: [String: Double] {
        switch dimension {
        case .app:
            return perDayFilteredAppSeconds
        case .all:
            return perDayInteraction
        default:
            return daySummaries.mapValues { heatValue(for: $0) }
        }
    }

    /// 今日窗口活动秒数（真实会话时长，供概览卡片）
    var todayAppSeconds: Double {
        appSecondsByDay[today] ?? 0
    }

    /// 选中日期窗口活动秒数（供概览卡片；日粒度跟随 selectedDate）
    var selectedDayAppSeconds: Double {
        appSecondsByDay[selectedDay] ?? 0
    }

    func valueText(_ value: Double) -> String {
        switch dimension {
        case .keyboard: return "\(Int(value)) 次按键"
        case .mouse:    return "\(Int(value)) 次鼠标操作"
        case .app, .all:
            let h = Int(value) / 3600
            let m = (Int(value) % 3600) / 60
            return h > 0 ? "\(h)小时\(m)分" : "\(m) 分钟"
        }
    }

    // MARK: - 范围计算

    /// 当前粒度的日期范围（含起止）
    func currentRange() -> (start: Date, end: Date) {
        let now = Date()
        let cal = calendar
        switch granularity {
        case .day:
            // 日粒度跟随选中日期（整天；起止同刻，查询端按 +1 天覆盖）
            let start = cal.startOfDay(for: selectedDate)
            return (start, start)
        case .month:
            let comps = cal.dateComponents([.year, .month], from: now)
            let start = cal.date(from: comps)!
            let end = cal.date(byAdding: DateComponents(month: 1, day: -1), to: start)!
            return (start, end)
        case .quarter:
            let comps = cal.dateComponents([.year, .month], from: now)
            let quarterFirstMonth = ((comps.month! - 1) / 3) * 3 + 1
            let start = cal.date(from: DateComponents(year: comps.year!, month: quarterFirstMonth, day: 1))!
            let end = cal.date(byAdding: DateComponents(month: 3, day: -1), to: start)!
            return (start, end)
        case .year:
            let year = cal.component(.year, from: now)
            let start = cal.date(from: DateComponents(year: year, month: 1, day: 1))!
            let end = cal.date(from: DateComponents(year: year, month: 12, day: 31))!
            return (start, end)
        }
    }

    /// 周网格：起始日期（对齐到周首）与总天数
    func weekGridParams() -> (alignedStart: Date, days: Int) {
        let (start, end) = currentRange()
        let cal = calendar
        let startOfStartDay = cal.startOfDay(for: start)
        // 对齐到该周周一
        let weekday = cal.component(.weekday, from: startOfStartDay) // 周日=1 ... 周六=7
        let offsetFromMonday = (weekday + 5) % 7
        let alignedStart = cal.date(byAdding: .day, value: -offsetFromMonday, to: startOfStartDay)!
        let days = cal.dateComponents([.day], from: alignedStart, to: cal.startOfDay(for: end)).day! + 1
        return (alignedStart, days)
    }

    // MARK: - 数据加载

    func reload() {
        let repo = repository
        let gran = granularity
        let dim = dimension
        let filter = categoryFilter
        let (rangeStart, rangeEnd) = currentRange()
        let startDay = EventRepository.dayString(for: rangeStart)
        let endDay = EventRepository.dayString(for: rangeEnd)
        // 日粒度的全部查询以选中日期为准（默认今天）
        let selDay = selectedDay
        let selDate = selectedDate
        // 周网格热力首周对齐周一后会包含范围外几天（如 8 月视图含 7.27–7.31），
        // 热力数据源须按对齐后的起始日取数，否则这些格子错误显示 0
        let gridStartDay = gran == .day ? startDay : EventRepository.dayString(for: weekGridParams().alignedStart)

        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let self else { return }

            // 日聚合（热力网格数据源，按对齐范围取数）
            let summaries = repo.daySummaries(from: gridStartDay, to: endDay)
            // 窗口活动时长（真实会话，按天聚合，供概览卡片）
            let appSeconds = repo.appSecondsByDay(from: gridStartDay, to: endDay)

            // 维度对应的事件种类
            let opKinds: [String]      // 操作频次口径（严格：剔除 keyUp/修饰键/自动重复）
            let activeKinds: [String]  // 活动时长口径（该来源任意事件即算活动）
            let includeTrack: Bool
            switch dim {
            case .keyboard:
                opKinds = ["keyDown"]
                activeKinds = ["keyDown", "keyUp", "flagsChanged"]
                includeTrack = false
            case .mouse:
                opKinds = ["leftClick", "rightClick", "scroll"]
                activeKinds = opKinds
                includeTrack = true
            case .app:
                opKinds = []
                activeKinds = []
                includeTrack = false
            case .all:
                opKinds = ["keyDown", "leftClick", "rightClick", "scroll"]
                activeKinds = []
                includeTrack = false
            }

            // 活动时长序列（日粒度分小时 / 范围粒度按天；应用与所有维度带类型筛选）
            var hourlyActivityData: [(hour: Int, seconds: Double)] = []
            var perDayActivityData: [(day: String, seconds: Double)] = []
            if gran == .day {
                let map: [Int: Double]
                switch dim {
                case .keyboard, .mouse:
                    map = repo.hourlyEventActiveSeconds(day: selDay, kinds: activeKinds, includeTrackPoints: includeTrack)
                case .app:
                    map = repo.hourlyAppSeconds(day: selDay, categoryFilter: filter)
                case .all:
                    map = repo.hourlyInteractionSeconds(day: selDay, categoryFilter: filter)
                }
                hourlyActivityData = (0...23).map { (hour: $0, seconds: map[$0] ?? 0) }
            } else {
                let counts: [String: Double]
                switch dim {
                case .keyboard, .mouse:
                    counts = repo.perDayEventActiveSeconds(from: startDay, to: endDay, kinds: activeKinds, includeTrackPoints: includeTrack)
                case .app:
                    counts = repo.perDayAppSeconds(from: startDay, to: endDay, categoryFilter: filter)
                case .all:
                    counts = repo.perDayInteractionSeconds(from: startDay, to: endDay, categoryFilter: filter)
                }
                perDayActivityData = Self.fillDays(from: rangeStart, to: rangeEnd).map { (day: $0, seconds: counts[$0] ?? 0) }
            }

            // 近 7 天分小时热力（以选中日期为终点向前 6 天）
            var hourlyData: [(String, [Int: Double])] = []
            if gran == .day {
                for offset in (0..<7).reversed() {
                    guard let date = Calendar.current.date(byAdding: .day, value: -offset, to: selDate) else { continue }
                    let day = EventRepository.dayString(for: date)
                    let hourly: [Int: Double]
                    switch dim {
                    case .keyboard:
                        hourly = repo.hourlyCounts(day: day, kinds: ["keyDown"]).mapValues { Double($0) }
                    case .mouse:
                        hourly = repo.hourlyCounts(day: day, kinds: ["leftClick", "rightClick", "scroll"]).mapValues { Double($0) }
                    case .app:
                        hourly = repo.hourlyAppSeconds(day: day, categoryFilter: filter)
                    case .all:
                        hourly = repo.hourlyInteractionSeconds(day: day, categoryFilter: filter)
                    }
                    hourlyData.append((day, hourly))
                }
            }

            // 周网格热力数据源（范围粒度，按对齐范围取数）
            let gridAppSeconds = (gran != .day && dim == .app)
                ? repo.perDayAppSeconds(from: gridStartDay, to: endDay, categoryFilter: filter) : [:]
            let gridInteraction = (gran != .day && dim == .all)
                ? repo.perDayInteractionSeconds(from: gridStartDay, to: endDay, categoryFilter: filter) : [:]

            // 维度明细
            let heat = (gran == .day && dim == .keyboard) ? repo.keyHeat(day: selDay) : []
            let points = (gran == .day && dim == .mouse) ? repo.trackPoints(day: selDay) : []

            // 操作频次（严格口径；应用维度=窗口切换次数；所有=三者合计）
            let minuteSeries: [(minute: Int, count: Int)]
            let daySeries: [(day: String, count: Int)]
            if gran == .day {
                if dim == .app {
                    minuteSeries = repo.perMinuteSessionStarts(day: selDay)
                } else {
                    var merged: [Int: Int] = Dictionary(repo.perMinuteCounts(day: selDay, kinds: opKinds), uniquingKeysWith: +)
                    if dim == .all {
                        for item in repo.perMinuteSessionStarts(day: selDay) {
                            merged[item.minute, default: 0] += item.count
                        }
                    }
                    minuteSeries = merged.map { (minute: $0.key, count: $0.value) }.sorted { $0.minute < $1.minute }
                }
                daySeries = []
            } else {
                minuteSeries = []
                var counts = dim == .app
                    ? repo.perDaySessionStarts(from: startDay, to: endDay)
                    : repo.perDayOperationCounts(from: startDay, to: endDay, kinds: opKinds)
                if dim == .all {
                    for (day, count) in repo.perDaySessionStarts(from: startDay, to: endDay) {
                        counts[day, default: 0] += count
                    }
                }
                daySeries = Self.fillDays(from: rangeStart, to: rangeEnd).map { (day: $0, count: counts[$0] ?? 0) }
            }

            // 选中日期互动时长（DB 分钟桶去重；供概览卡在历史日期下展示，今天由 UI 用实时值覆盖）
            let selInteraction = (gran == .day) ? repo.interactionSeconds(day: selDay) : 0

            // 应用使用（范围粒度）：查询时按覆盖表实时重映射分类（配置对历史数据立即生效），再按类型筛选
            var usage = repo.appUsage(from: rangeStart.timeIntervalSince1970, to: rangeEnd.timeIntervalSince1970 + 86400)
                .map { AppUsageSummary(
                    appName: $0.appName,
                    bundleID: $0.bundleID,
                    category: AppCategoryMap.shared.category(for: $0.bundleID),
                    totalSeconds: $0.totalSeconds
                ) }
            if let filter {
                usage = usage.filter { $0.category == filter }
            }

            DispatchQueue.main.async {
                self.daySummaries = summaries
                self.appSecondsByDay = appSeconds
                self.perDayFilteredAppSeconds = gridAppSeconds
                self.perDayInteraction = gridInteraction
                self.hourlyActivity = hourlyActivityData
                self.perDayActivity = perDayActivityData
                self.recent7Hourly = hourlyData
                self.keyHeat = heat
                self.trackPoints = points
                self.appUsage = usage
                self.perMinute = minuteSeries
                self.perDayFrequency = daySeries
                self.selectedDayInteraction = selInteraction
            }
        }
    }

    /// 范围内连续的日期字符串序列（含无数据天）
    private static func fillDays(from start: Date, to end: Date) -> [String] {
        var days: [String] = []
        var cursor = start
        let cal = Calendar.current
        while cursor <= end {
            days.append(EventRepository.dayString(for: cursor))
            guard let next = cal.date(byAdding: .day, value: 1, to: cursor) else { break }
            cursor = next
        }
        return days
    }
}
