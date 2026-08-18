import AppKit
import SwiftUI

/// 统计主面板：今日概览 + 日/月/季/年热力 + 维度明细 + 应用类型统计
struct StatsWindowView: View {
    @ObservedObject var viewModel: StatsViewModel
    /// 今日互动总秒数（与状态栏同源的实时值）
    var interactionSeconds: Double = 0

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            controls
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
            Divider()
            ScrollView {
                content
                    .padding(20)
            }
            .background(DashboardTheme.pageBackground)
        }
        .frame(minWidth: 900, minHeight: 600)
        .onAppear {
            viewModel.reload()
            viewModel.startAutoRefresh()
        }
        .onDisappear {
            // 切到设置页（详情区被替换）时停止自动刷新
            viewModel.stopAutoRefresh()
        }
        // 面板窗口由 NSWindowController 常驻持有，关窗不一定触发 onDisappear，须靠通知补齐生命周期
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.willCloseNotification)) { note in
            if (note.object as? NSWindow)?.identifier?.rawValue == "main" {
                viewModel.stopAutoRefresh()
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didBecomeKeyNotification)) { note in
            // 重开面板（窗口常驻，onAppear 不再触发）：回到前台即刷新数据并恢复自动刷新
            if (note.object as? NSWindow)?.identifier?.rawValue == "main" {
                viewModel.reload()
                viewModel.startAutoRefresh()
            }
        }
    }

    // MARK: - 概览（日粒度跟随选中日期，范围粒度恒为今天）

    private var header: some View {
        let isDayGranularity = viewModel.granularity == .day
        let headerDay = isDayGranularity ? viewModel.selectedDay : viewModel.today
        let summary = viewModel.daySummaries[headerDay]
        // 互动时长：今天的用 AppState 实时值（含未落库部分），历史日期用 DB 分钟桶去重值
        let showLiveInteraction = !isDayGranularity || viewModel.isSelectedToday
        return HStack(spacing: 16) {
            OverviewCard(
                title: "键盘",
                icon: "keyboard",
                accent: DashboardTheme.accentBlue,
                primary: "\(summary?.keyCount ?? 0)",
                secondary: "次按键 · 按住 \(formatDuration(ms: summary?.keyDurationMs ?? 0))"
            )
            OverviewCard(
                title: "鼠标",
                icon: "computermouse",
                accent: DashboardTheme.accentIndigo,
                primary: "\((summary?.clickCount ?? 0) + (summary?.scrollCount ?? 0))",
                secondary: "次操作 · 移动 \(formatDistance(summary?.moveDistance ?? 0))"
            )
            OverviewCard(
                title: "窗口活动",
                icon: "macwindow",
                accent: DashboardTheme.accentGreen,
                primary: formatSeconds(isDayGranularity ? viewModel.selectedDayAppSeconds : viewModel.todayAppSeconds),
                secondary: "按前台会话统计"
            )
            OverviewCard(
                title: "互动时长",
                icon: "clock.badge.checkmark",
                accent: Color(red: 1.00, green: 0.62, blue: 0.04),
                primary: formatSeconds(showLiveInteraction ? interactionSeconds : viewModel.selectedDayInteraction),
                secondary: "键鼠与窗口活动去重"
            )
        }
        .padding(20)
        .background(DashboardTheme.pageBackground)
    }

    // MARK: - 控制条

    private var controls: some View {
        HStack(spacing: 16) {
            Picker("粒度", selection: $viewModel.granularity) {
                ForEach(StatsViewModel.Granularity.allCases) { g in
                    Text(g.rawValue).tag(g)
                }
            }
            .pickerStyle(.segmented)
            .frame(width: 220)

            Picker("维度", selection: $viewModel.dimension) {
                ForEach(StatsViewModel.Dimension.allCases) { d in
                    Text(d.rawValue).tag(d)
                }
            }
            .pickerStyle(.segmented)
            .frame(width: 220)

            if viewModel.granularity == .day {
                DatePicker("", selection: $viewModel.selectedDate, in: ...Date(), displayedComponents: .date)
                    .labelsHidden()
                    .datePickerStyle(.compact)
                if !viewModel.isSelectedToday {
                    Button("回到今天") { viewModel.selectedDate = Date() }
                        .controlSize(.small)
                }
            }

            Spacer()

            Picker("应用类型", selection: $viewModel.categoryFilter) {
                Text("全部类型").tag(AppCategory?.none)
                ForEach(AppCategory.allCases, id: \.self) { c in
                    Text(c.displayName).tag(AppCategory?.some(c))
                }
            }
            .frame(width: 130)
        }
    }

    // MARK: - 内容区

    @ViewBuilder
    private var content: some View {
        switch viewModel.granularity {
        case .day:
            dayContent
        case .month, .quarter, .year:
            rangeContent
        }
    }

    /// 操作频次的单位文案（按维度）
    private var frequencyUnit: String {
        switch viewModel.dimension {
        case .keyboard: return "次按键"
        case .mouse:    return "次操作"
        case .app:      return "次窗口切换"
        case .all:      return "次操作"
        }
    }

    private var dayContent: some View {
        VStack(alignment: .leading, spacing: 16) {
            HourlyActivityBarView(
                hourly: viewModel.hourlyActivity,
                perDay: []
            )

            FrequencyChartView(
                perMinute: viewModel.perMinute,
                perDay: [],
                unitText: frequencyUnit
            )

            VStack(alignment: .leading, spacing: 8) {
                Text("近 7 天分小时热力 · \(viewModel.dimension.rawValue)")
                    .font(.system(size: 13, weight: .semibold))
                HourlyHeatmapView(
                    data: viewModel.recent7Hourly,
                    valueFormatter: viewModel.valueText
                )
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(16)
            .dashboardCard()

            switch viewModel.dimension {
            case .keyboard:
                KeyHeatmapView(items: viewModel.keyHeat)
            case .mouse:
                TrackHeatmapView(points: viewModel.trackPoints, dayLabel: selectedDayLabel)
            case .app, .all:
                AppUsageListView(appUsage: viewModel.appUsage)
            }
        }
    }

    /// 选中日期文案（今日 / M月d日），用于日粒度图表标题
    private var selectedDayLabel: String {
        if viewModel.isSelectedToday { return "今日" }
        let comps = Calendar.current.dateComponents([.month, .day], from: viewModel.selectedDate)
        return "\(comps.month ?? 0)月\(comps.day ?? 0)日"
    }

    private var rangeContent: some View {
        let params = viewModel.weekGridParams()
        return VStack(alignment: .leading, spacing: 16) {
            HourlyActivityBarView(
                hourly: [],
                perDay: viewModel.perDayActivity
            )

            FrequencyChartView(
                perMinute: [],
                perDay: viewModel.perDayFrequency,
                unitText: frequencyUnit
            )

            VStack(alignment: .leading, spacing: 8) {
                Text("\(viewModel.granularity.rawValue)度热力 · \(viewModel.dimension.rawValue)")
                    .font(.system(size: 13, weight: .semibold))
                WeekGridHeatmapView(
                    alignedStart: params.alignedStart,
                    days: params.days,
                    dayValues: viewModel.heatDayValues,
                    valueFormatter: viewModel.valueText
                )
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(16)
            .dashboardCard()

            CategoryBreakdownView(appUsage: viewModel.appUsage)
            AppUsageListView(appUsage: viewModel.appUsage)
        }
    }

    // MARK: - 格式化

    private func formatDuration(ms: Int64) -> String {
        let seconds = ms / 1000
        if seconds < 60 { return "\(seconds)秒" }
        return "\(seconds / 60)分\(seconds % 60)秒"
    }

    private func formatDistance(_ px: Double) -> String {
        if px < 1000 { return "\(Int(px))px" }
        if px < 1_000_000 { return String(format: "%.1fk px", px / 1000) }
        return String(format: "%.2fM px", px / 1_000_000)
    }

    private func formatSeconds(_ s: Double) -> String {
        let h = Int(s) / 3600
        let m = (Int(s) % 3600) / 60
        return h > 0 ? "\(h)小时\(m)分" : "\(m)分钟"
    }
}

/// 今日概览卡片
private struct OverviewCard: View {
    let title: String
    let icon: String
    let accent: Color
    let primary: String
    let secondary: String

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: icon)
                .font(.system(size: 18, weight: .medium))
                .foregroundColor(.white)
                .frame(width: 42, height: 42)
                .background(
                    LinearGradient(
                        colors: [accent, accent.opacity(0.72)],
                        startPoint: .topLeading,
                        endPoint: .bottomTrailing
                    ),
                    in: RoundedRectangle(cornerRadius: 11, style: .continuous)
                )
            VStack(alignment: .leading, spacing: 3) {
                Text(title)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(.secondary)
                Text(primary)
                    .font(.system(size: 22, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
                Text(secondary)
                    .font(.system(size: 10))
                    .foregroundColor(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
            }
            Spacer()
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
        .frame(maxWidth: .infinity, minHeight: 78, maxHeight: 78)
        .dashboardCard()
    }
}

/// 应用使用排行
struct AppUsageListView: View {
    let appUsage: [AppUsageSummary]

    private var maxSeconds: Double { appUsage.map(\.totalSeconds).max() ?? 1 }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("应用使用时长")
                .font(.system(size: 15, weight: .semibold))

            if appUsage.isEmpty {
                Text("暂无应用使用数据")
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
                    .padding(.vertical, 20)
                    .frame(maxWidth: .infinity)
            } else {
                LazyVStack(spacing: 8) {
                    ForEach(appUsage.prefix(15), id: \.bundleID) { item in
                        HStack(spacing: 10) {
                            Text(item.appName)
                                .font(.system(size: 12, weight: .medium))
                                .frame(width: 160, alignment: .leading)
                                .lineLimit(1)
                            Text(item.category.displayName)
                                .font(.system(size: 10))
                                .foregroundColor(.secondary)
                                .padding(.horizontal, 6)
                                .padding(.vertical, 2)
                                .background(DashboardTheme.tagBackground, in: Capsule())
                            GeometryReader { geo in
                                RoundedRectangle(cornerRadius: 3)
                                    .fill(DashboardTheme.accentGreen.opacity(0.75))
                                    .frame(width: max(2, geo.size.width * item.totalSeconds / maxSeconds))
                            }
                            .frame(height: 10)
                            Text(formatSeconds(item.totalSeconds))
                                .font(.system(size: 11, design: .monospaced))
                                .foregroundColor(.secondary)
                                .frame(width: 72, alignment: .trailing)
                        }
                        .help("\(item.appName)：\(formatSeconds(item.totalSeconds))")
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .dashboardCard()
    }

    private func formatSeconds(_ s: Double) -> String {
        let h = Int(s) / 3600
        let m = (Int(s) % 3600) / 60
        return h > 0 ? "\(h)h\(m)m" : "\(m)m"
    }
}

/// 应用类型时长分布
struct CategoryBreakdownView: View {
    let appUsage: [AppUsageSummary]

    private var breakdown: [(category: AppCategory, seconds: Double)] {
        var map: [AppCategory: Double] = [:]
        for item in appUsage {
            map[item.category, default: 0] += item.totalSeconds
        }
        return map.map { ($0.key, $0.value) }.sorted { $0.seconds > $1.seconds }
    }

    private var total: Double { breakdown.map(\.seconds).reduce(0, +) }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("应用类型分布")
                .font(.system(size: 15, weight: .semibold))

            if breakdown.isEmpty {
                Text("暂无数据")
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
                    .padding(.vertical, 20)
                    .frame(maxWidth: .infinity)
            } else {
                // 汇总比例条
                GeometryReader { geo in
                    HStack(spacing: 1) {
                        ForEach(breakdown, id: \.category) { item in
                            RoundedRectangle(cornerRadius: 2)
                                .fill(color(for: item.category))
                                .frame(width: geo.size.width * item.seconds / max(total, 1))
                                .help("\(item.category.displayName) \(Int(item.seconds / total * 100))%")
                        }
                    }
                }
                .frame(height: 14)

                LazyVStack(spacing: 6) {
                    ForEach(breakdown, id: \.category) { item in
                        HStack(spacing: 8) {
                            Circle()
                                .fill(color(for: item.category))
                                .frame(width: 8, height: 8)
                            Text(item.category.displayName)
                                .font(.system(size: 12))
                            Spacer()
                            Text(formatSeconds(item.seconds))
                                .font(.system(size: 11, design: .monospaced))
                                .foregroundColor(.secondary)
                            Text("\(Int(item.seconds / max(total, 1) * 100))%")
                                .font(.system(size: 11, design: .monospaced))
                                .foregroundColor(.secondary)
                                .frame(width: 40, alignment: .trailing)
                        }
                        .help("\(item.category.displayName)：\(formatSeconds(item.seconds))（\(Int(item.seconds / max(total, 1) * 100))%）")
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .dashboardCard()
    }

    private func color(for category: AppCategory) -> Color {
        switch category {
        case .development: return DashboardTheme.accentBlue
        case .browser:     return DashboardTheme.accentIndigo
        case .office:      return DashboardTheme.accentGreen
        case .design:      return Color(red: 1.00, green: 0.62, blue: 0.04)
        case .social:      return Color(red: 1.00, green: 0.27, blue: 0.23)
        case .media:       return Color(red: 0.69, green: 0.32, blue: 0.87)
        case .other:       return DashboardTheme.neutral
        }
    }

    private func formatSeconds(_ s: Double) -> String {
        let h = Int(s) / 3600
        let m = (Int(s) % 3600) / 60
        return h > 0 ? "\(h)h\(m)m" : "\(m)m"
    }
}
