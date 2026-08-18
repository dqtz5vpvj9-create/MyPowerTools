import Charts
import SwiftUI

/// 操作频次曲线（严格口径：键盘=按键次数、鼠标=点击+滚轮、应用=窗口切换次数）
/// - 日粒度：10 分钟分桶曲线；月/季/年：按天曲线
/// - 悬浮显示对应时间点/日期的具体数值
struct FrequencyChartView: View {
    /// 日模式：分钟序列（原始 1 分钟粒度，内部聚合为 10 分钟）
    let perMinute: [(minute: Int, count: Int)]
    /// 范围模式：按天序列
    let perDay: [(day: String, count: Int)]
    /// 单位文案（"次按键" / "次操作" / "次窗口切换"）
    let unitText: String

    @State private var hoverMinute: Int?
    @State private var hoverDay: String?

    private var isDayMode: Bool { !perMinute.isEmpty || perDay.isEmpty }

    /// 聚合到 10 分钟粒度，曲线更平滑可读
    private var buckets: [(minute: Int, count: Int)] {
        var map: [Int: Int] = [:]
        for item in perMinute {
            let bucket = item.minute / 10
            map[bucket, default: 0] += item.count
        }
        return map.map { (minute: $0.key * 10, count: $0.value) }.sorted { $0.minute < $1.minute }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("操作频次")
                .font(.system(size: 13, weight: .semibold))

            if isDayMode {
                dayChart
            } else {
                rangeChart
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .dashboardCard()
    }

    // MARK: - 日模式（10 分钟分桶）

    @ViewBuilder
    private var dayChart: some View {
        if perMinute.isEmpty {
            emptyHint
        } else {
            Chart {
                ForEach(buckets, id: \.minute) { item in
                    AreaMark(
                        x: .value("时间", item.minute),
                        y: .value("次数", item.count)
                    )
                    .foregroundStyle(
                        LinearGradient(
                            colors: [
                                DashboardTheme.accentBlue.opacity(0.35),
                                DashboardTheme.accentIndigo.opacity(0.05)
                            ],
                            startPoint: .top,
                            endPoint: .bottom
                        )
                    )
                    LineMark(
                        x: .value("时间", item.minute),
                        y: .value("次数", item.count)
                    )
                    .foregroundStyle(DashboardTheme.accentBlue)
                    .lineStyle(StrokeStyle(lineWidth: 1.5))
                }
                if let hoverMinute, let item = nearestBucket(to: hoverMinute) {
                    RuleMark(x: .value("时间", item.minute))
                        .foregroundStyle(Color.secondary.opacity(0.4))
                        .lineStyle(StrokeStyle(lineWidth: 1, dash: [3, 3]))
                        .annotation(position: .top, spacing: 4) {
                            chartAnnotation(
                                title: minuteLabel(item.minute),
                                value: "\(item.count) \(unitText)"
                            )
                        }
                }
            }
            .chartXScale(domain: 0...1440)
            .chartXAxis {
                AxisMarks(values: [0, 360, 720, 1080, 1439]) { value in
                    AxisValueLabel {
                        if let minute = value.as(Int.self) {
                            Text(String(format: "%02d:00", min(minute, 1439) / 60))
                                .font(.system(size: 9))
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
                                hoverMinute = proxy.value(atX: location.x)
                            case .ended:
                                hoverMinute = nil
                            }
                        }
                }
            }
            .frame(height: 160)
        }
    }

    // MARK: - 范围模式（按天）

    @ViewBuilder
    private var rangeChart: some View {
        if perDay.isEmpty {
            emptyHint
        } else {
            Chart {
                ForEach(Array(perDay.enumerated()), id: \.offset) { index, item in
                    AreaMark(
                        x: .value("日期", index),
                        y: .value("次数", item.count)
                    )
                    .foregroundStyle(
                        LinearGradient(
                            colors: [
                                DashboardTheme.accentBlue.opacity(0.35),
                                DashboardTheme.accentIndigo.opacity(0.05)
                            ],
                            startPoint: .top,
                            endPoint: .bottom
                        )
                    )
                    LineMark(
                        x: .value("日期", index),
                        y: .value("次数", item.count)
                    )
                    .foregroundStyle(DashboardTheme.accentBlue)
                    .lineStyle(StrokeStyle(lineWidth: 1.5))
                }
                if let hoverDay, let index = perDay.firstIndex(where: { $0.day == hoverDay }) {
                    RuleMark(x: .value("日期", index))
                        .foregroundStyle(Color.secondary.opacity(0.4))
                        .lineStyle(StrokeStyle(lineWidth: 1, dash: [3, 3]))
                        .annotation(position: .top, spacing: 4) {
                            chartAnnotation(
                                title: String(hoverDay.suffix(5)),
                                value: "\(perDay[index].count) \(unitText)"
                            )
                        }
                }
            }
            .chartXScale(domain: 0...max(perDay.count - 1, 1))
            .chartXAxis {
                let strideBy = max(1, perDay.count / 6)
                AxisMarks(values: Swift.stride(from: 0, to: max(perDay.count, 1), by: strideBy).map { $0 }) { value in
                    AxisValueLabel {
                        if let index = value.as(Int.self), perDay.indices.contains(index) {
                            Text(String(perDay[index].day.suffix(5)))
                                .font(.system(size: 9))
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
                                if let index: Int = proxy.value(atX: location.x),
                                   perDay.indices.contains(index) {
                                    hoverDay = perDay[index].day
                                }
                            case .ended:
                                hoverDay = nil
                            }
                        }
                }
            }
            .frame(height: 160)
        }
    }

    // MARK: - 组件

    private var emptyHint: some View {
        Text("暂无数据")
            .font(.system(size: 12))
            .foregroundColor(.secondary)
            .frame(height: 160)
            .frame(maxWidth: .infinity)
    }

    private func chartAnnotation(title: String, value: String) -> some View {
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

    private func nearestBucket(to minute: Int) -> (minute: Int, count: Int)? {
        buckets.min(by: { abs($0.minute - minute) < abs($1.minute - minute) })
    }

    private func minuteLabel(_ minute: Int) -> String {
        String(format: "%02d:%02d", minute / 60, minute % 60)
    }
}
