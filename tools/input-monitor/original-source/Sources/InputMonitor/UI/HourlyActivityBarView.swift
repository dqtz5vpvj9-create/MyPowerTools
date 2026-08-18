import Charts
import SwiftUI

/// 活动时长柱状图（随粒度/维度/应用类型筛选）
/// - 日粒度：24 小时柱；月/季/年：按天柱
/// - 悬浮显示对应时段的具体活动时长
struct HourlyActivityBarView: View {
    /// 日模式：0...23 每小时活动秒数
    let hourly: [(hour: Int, seconds: Double)]
    /// 范围模式：按天活动秒数
    let perDay: [(day: String, seconds: Double)]

    @State private var hoverHour: Int?
    @State private var hoverDay: String?

    private var isDayMode: Bool { perDay.isEmpty }
    private var hasData: Bool {
        isDayMode ? hourly.contains { $0.seconds > 0 } : perDay.contains { $0.seconds > 0 }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("活动时长")
                .font(.system(size: 13, weight: .semibold))

            if !hasData {
                Text("暂无数据")
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
                    .frame(height: 140)
                    .frame(maxWidth: .infinity)
            } else if isDayMode {
                dayChart
            } else {
                rangeChart
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .dashboardCard()
    }

    // MARK: - 日模式（分小时）

    private var dayChart: some View {
        Chart {
            ForEach(hourly, id: \.hour) { item in
                BarMark(
                    x: .value("时", item.hour),
                    y: .value("时长", item.seconds / 60)
                )
                .foregroundStyle(
                    item.hour == hoverHour
                        ? DashboardTheme.accentBlue
                        : DashboardTheme.accentBlue.opacity(0.75)
                )
                .cornerRadius(3)
            }
            if let hoverHour, let item = hourly.first(where: { $0.hour == hoverHour }) {
                RuleMark(x: .value("时", item.hour))
                    .foregroundStyle(Color.secondary.opacity(0.4))
                    .lineStyle(StrokeStyle(lineWidth: 1, dash: [3, 3]))
                    .annotation(position: .top, spacing: 4) {
                        annotationView(title: "\(item.hour):00 – \(item.hour + 1):00", seconds: item.seconds)
                    }
            }
        }
        .chartXScale(domain: -0.5...23.5)
        .chartXAxis {
            AxisMarks(values: [0, 3, 6, 9, 12, 15, 18, 21]) { value in
                AxisValueLabel {
                    if let hour = value.as(Int.self) {
                        Text("\(hour)").font(.system(size: 9))
                    }
                }
                AxisGridLine()
            }
        }
        .chartYAxis { minuteAxis }
        .chartOverlay { proxy in
            GeometryReader { _ in
                Rectangle()
                    .fill(.clear)
                    .contentShape(Rectangle())
                    .onContinuousHover { phase in
                        switch phase {
                        case .active(let location):
                            if let hour: Int = proxy.value(atX: location.x), (0...23).contains(hour) {
                                hoverHour = hour
                            } else {
                                hoverHour = nil
                            }
                        case .ended:
                            hoverHour = nil
                        }
                    }
            }
        }
        .frame(height: 140)
    }

    // MARK: - 范围模式（按天）

    private var rangeChart: some View {
        Chart {
            ForEach(Array(perDay.enumerated()), id: \.offset) { index, item in
                BarMark(
                    x: .value("日期", index),
                    y: .value("时长", item.seconds / 3600)
                )
                .foregroundStyle(
                    item.day == hoverDay
                        ? DashboardTheme.accentBlue
                        : DashboardTheme.accentBlue.opacity(0.75)
                )
                .cornerRadius(2)
            }
            if let hoverDay, let index = perDay.firstIndex(where: { $0.day == hoverDay }) {
                RuleMark(x: .value("日期", index))
                    .foregroundStyle(Color.secondary.opacity(0.4))
                    .lineStyle(StrokeStyle(lineWidth: 1, dash: [3, 3]))
                    .annotation(position: .top, spacing: 4) {
                        annotationView(title: hoverDay, seconds: perDay[index].seconds)
                    }
            }
        }
        .chartXScale(domain: -0.5...Double(max(perDay.count, 1)) - 0.5)
        .chartXAxis {
            let strideBy = max(1, perDay.count / 8)
            AxisMarks(values: Swift.stride(from: 0, to: max(perDay.count, 1), by: strideBy).map { $0 }) { value in
                AxisValueLabel {
                    if let index = value.as(Int.self), perDay.indices.contains(index) {
                        Text(String(perDay[index].day.suffix(5))).font(.system(size: 9))
                    }
                }
                AxisGridLine()
            }
        }
        .chartYAxis {
            AxisMarks { value in
                AxisValueLabel {
                    if let hours = value.as(Double.self) {
                        Text("\(Int(hours))h").font(.system(size: 9))
                    }
                }
                AxisGridLine()
            }
        }
        .chartOverlay { proxy in
            GeometryReader { _ in
                Rectangle()
                    .fill(.clear)
                    .contentShape(Rectangle())
                    .onContinuousHover { phase in
                        switch phase {
                        case .active(let location):
                            if let index: Int = proxy.value(atX: location.x), perDay.indices.contains(index) {
                                hoverDay = perDay[index].day
                            } else {
                                hoverDay = nil
                            }
                        case .ended:
                            hoverDay = nil
                        }
                    }
            }
        }
        .frame(height: 140)
    }

    // MARK: - 组件

    private var minuteAxis: some AxisContent {
        AxisMarks { value in
            AxisValueLabel {
                if let minutes = value.as(Double.self) {
                    Text("\(Int(minutes))m").font(.system(size: 9))
                }
            }
            AxisGridLine()
        }
    }

    private func annotationView(title: String, seconds: Double) -> some View {
        VStack(spacing: 1) {
            Text(title)
                .font(.system(size: 9))
                .foregroundColor(.secondary)
            Text(durationText(seconds))
                .font(.system(size: 10, weight: .semibold, design: .rounded))
                .monospacedDigit()
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .background(DashboardTheme.cardBackground, in: RoundedRectangle(cornerRadius: 6))
        .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Color.primary.opacity(0.1), lineWidth: 1))
        .shadow(color: .black.opacity(0.08), radius: 4, y: 2)
    }

    private func durationText(_ seconds: Double) -> String {
        let total = Int(seconds)
        let h = total / 3600
        let m = (total % 3600) / 60
        let s = total % 60
        if h > 0 { return "\(h) 小时 \(m) 分" }
        if m == 0 { return "\(s) 秒" }
        return s == 0 ? "\(m) 分钟" : "\(m) 分 \(s) 秒"
    }
}
