import SwiftUI

/// 热力颜色：灰底 → 浅蓝 → 蓝紫 单色梯度递进（浅色/深色自适应）
enum HeatColor {
    static func color(value: Double, max maxValue: Double) -> Color {
        DashboardTheme.heatColor(value: value, max: maxValue)
    }
}

/// 悬浮统计气泡：与折线图 annotation 同款样式（标题 + 数值卡片）
struct HeatmapHoverBadge: View {
    let title: String
    let value: String

    var body: some View {
        VStack(spacing: 1) {
            Text(title)
                .font(.system(size: 9))
                .foregroundColor(.secondary)
            Text(value)
                .font(.system(size: 10, weight: .semibold, design: .rounded))
                .monospacedDigit()
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .background(DashboardTheme.cardBackground, in: RoundedRectangle(cornerRadius: 6))
        .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Color.primary.opacity(0.1), lineWidth: 1))
        .shadow(color: .black.opacity(0.08), radius: 4, y: 2)
    }
}

/// 周网格热力图（GitHub 贡献图风格：列=周，行=周一~周日），月/季/年复用
/// 悬浮格子时展示该日期的具体数值（气泡画在内容顶部预留区）
/// 注意：不内嵌横向 ScrollView——横向滚动视图嵌在外层纵向 ScrollView 里，
/// 外层滚动到底时内层内容会被错误渲染到窗口顶部（残影，SwiftUI on macOS 渲染缺陷）。
/// 月/季/年最多 53 列，最小窗宽下均可容纳，单元格尺寸随宽度自适应微缩
struct WeekGridHeatmapView: View {
    let alignedStart: Date
    let days: Int
    let dayValues: [String: Double]
    let valueFormatter: (Double) -> String

    /// 单元格尺寸上限（宽度不足时自动微缩，最小 6）
    var maxCellSize: CGFloat = 13
    var cellSpacing: CGFloat = 3

    /// 顶部为悬浮气泡预留的空间（首行格子的气泡画在这里）
    private let tooltipSpace: CGFloat = 36
    private let labelWidth: CGFloat = 12

    @State private var hoverCell: (week: Int, weekday: Int)?

    private var calendar: Calendar {
        var c = Calendar(identifier: .gregorian)
        c.firstWeekday = 2
        return c
    }

    private var weeks: Int { Int(ceil(Double(days) / 7.0)) }
    private var maxValue: Double { max(dayValues.values.max() ?? 0, 1) }

    private static let weekdayLabels = ["一", "", "三", "", "五", "", "日"]

    /// 实际单元格尺寸：可用宽度容纳全部周列时取上限，否则按比例微缩
    private func cellSize(forAvailableWidth width: CGFloat) -> CGFloat {
        let available = width - labelWidth - cellSpacing
        let fitted = floor((available - cellSpacing * CGFloat(weeks - 1)) / CGFloat(weeks))
        return min(maxCellSize, max(6, fitted))
    }

    var body: some View {
        GeometryReader { geo in
            let cell = cellSize(forAvailableWidth: geo.size.width)
            HStack(alignment: .top, spacing: cellSpacing) {
                // 星期标签列
                VStack(spacing: cellSpacing) {
                    ForEach(0..<7, id: \.self) { i in
                        Text(Self.weekdayLabels[i])
                            .font(.system(size: 9))
                            .foregroundColor(.secondary)
                            .frame(width: labelWidth, height: cell)
                    }
                }
                .padding(.top, tooltipSpace)

                HStack(alignment: .top, spacing: cellSpacing) {
                    ForEach(0..<weeks, id: \.self) { week in
                        VStack(spacing: cellSpacing) {
                            ForEach(0..<7, id: \.self) { weekday in
                                self.cell(week: week, weekday: weekday, size: cell)
                            }
                        }
                    }
                }
                .padding(.top, tooltipSpace)
                .overlay(hoverLayer(cell: cell))
            }
        }
        .frame(height: tooltipSpace + 7 * (maxCellSize + cellSpacing))
    }

    @ViewBuilder
    private func cell(week: Int, weekday: Int, size: CGFloat) -> some View {
        let index = week * 7 + weekday
        if index < days, let date = calendar.date(byAdding: .day, value: index, to: alignedStart) {
            let key = EventRepository.dayString(for: date)
            let value = dayValues[key] ?? 0
            RoundedRectangle(cornerRadius: 3)
                .fill(HeatColor.color(value: value, max: maxValue))
                .frame(width: size, height: size)
        } else {
            Color.clear.frame(width: size, height: size)
        }
    }

    /// 悬浮探测 + 数值气泡（与格子同坐标系）
    private func hoverLayer(cell: CGFloat) -> some View {
        GeometryReader { geo in
            Rectangle()
                .fill(.clear)
                .contentShape(Rectangle())
                .onContinuousHover { phase in
                    switch phase {
                    case .active(let location):
                        let week = Int(floor(location.x / (cell + cellSpacing)))
                        let weekday = Int(floor((location.y - tooltipSpace) / (cell + cellSpacing)))
                        if (0..<weeks).contains(week), (0..<7).contains(weekday),
                           week * 7 + weekday < days {
                            hoverCell = (week, weekday)
                        } else {
                            hoverCell = nil
                        }
                    case .ended:
                        hoverCell = nil
                    }
                }
            if let hovered = hoverCell,
               let date = calendar.date(byAdding: .day, value: hovered.week * 7 + hovered.weekday, to: alignedStart) {
                let key = EventRepository.dayString(for: date)
                let value = dayValues[key] ?? 0
                let centerX = (CGFloat(hovered.week) + 0.5) * (cell + cellSpacing)
                let centerY = tooltipSpace + CGFloat(hovered.weekday) * (cell + cellSpacing) - 21
                HeatmapHoverBadge(title: key, value: valueFormatter(value))
                    .position(
                        x: min(max(centerX, 48), max(geo.size.width - 48, 48)),
                        y: max(centerY, 15)
                    )
                    .allowsHitTesting(false)
            }
        }
    }
}

/// 分小时热力图：最近 7 天 × 24 小时（行=日期，列=小时），宽度自适应填满卡片
/// 悬浮格子时展示该日期小时的具体数值
struct HourlyHeatmapView: View {
    let data: [(day: String, hourly: [Int: Double])]
    let valueFormatter: (Double) -> String

    var cellHeight: CGFloat = 22
    var cellSpacing: CGFloat = 2
    private let labelWidth: CGFloat = 44

    @State private var hoverCell: (row: Int, hour: Int)?

    private var maxValue: Double {
        data.flatMap { $0.hourly.values }.max() ?? 1
    }

    var body: some View {
        GeometryReader { geo in
            let cellWidth = max(6, (geo.size.width - labelWidth - cellSpacing * 24) / 24)
            VStack(alignment: .leading, spacing: cellSpacing) {
                // 小时表头（每 3 小时一个刻度，居中于列）
                HStack(spacing: cellSpacing) {
                    Text("").frame(width: labelWidth, alignment: .leading)
                    ForEach(0..<24, id: \.self) { hour in
                        Text(hour % 3 == 0 ? "\(hour)" : "")
                            .font(.system(size: 8))
                            .foregroundColor(.secondary)
                            .frame(width: cellWidth)
                    }
                }
                VStack(spacing: cellSpacing) {
                    ForEach(Array(data.enumerated()), id: \.offset) { _, item in
                        HStack(spacing: cellSpacing) {
                            Text(dayLabel(item.day))
                                .font(.system(size: 10))
                                .foregroundColor(.secondary)
                                .frame(width: labelWidth, alignment: .leading)
                            ForEach(0..<24, id: \.self) { hour in
                                let value = item.hourly[hour] ?? 0
                                RoundedRectangle(cornerRadius: 3)
                                    .fill(HeatColor.color(value: value, max: maxValue))
                                    .frame(width: cellWidth, height: cellHeight)
                            }
                        }
                    }
                }
                .overlay(hoverLayer(cellWidth: cellWidth))
            }
        }
        .frame(height: 14 + CGFloat(data.count) * (cellHeight + cellSpacing))
    }

    /// 悬浮探测 + 数值气泡（气泡向上溢出到表头区域，不会被裁切）
    private func hoverLayer(cellWidth: CGFloat) -> some View {
        GeometryReader { geo in
            Rectangle()
                .fill(.clear)
                .contentShape(Rectangle())
                .onContinuousHover { phase in
                    switch phase {
                    case .active(let location):
                        let row = Int(floor(location.y / (cellHeight + cellSpacing)))
                        let hour = Int(floor((location.x - labelWidth - cellSpacing) / (cellWidth + cellSpacing)))
                        if data.indices.contains(row), location.x >= labelWidth + cellSpacing {
                            hoverCell = (row, min(max(hour, 0), 23))
                        } else {
                            hoverCell = nil
                        }
                    case .ended:
                        hoverCell = nil
                    }
                }
            if let cell = hoverCell {
                let item = data[cell.row]
                let value = item.hourly[cell.hour] ?? 0
                let centerX = labelWidth + cellSpacing + (CGFloat(cell.hour) + 0.5) * (cellWidth + cellSpacing)
                let centerY = CGFloat(cell.row) * (cellHeight + cellSpacing) - 21
                HeatmapHoverBadge(
                    title: "\(dayLabel(item.day)) \(cell.hour):00",
                    value: valueFormatter(value)
                )
                .position(
                    x: min(max(centerX, 56), max(geo.size.width - 56, 56)),
                    y: centerY
                )
                .allowsHitTesting(false)
            }
        }
    }

    private func dayLabel(_ day: String) -> String {
        let today = EventRepository.dayString()
        if day == today { return "今天" }
        if let yesterday = Calendar.current.date(byAdding: .day, value: -1, to: Date()),
           EventRepository.dayString(for: yesterday) == day {
            return "昨天"
        }
        return String(day.suffix(5)) // MM-dd
    }
}
