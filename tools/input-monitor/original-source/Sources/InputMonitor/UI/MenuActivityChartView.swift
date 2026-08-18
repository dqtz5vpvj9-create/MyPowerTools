import Charts
import SwiftUI

/// 状态栏菜单内嵌图表的数据模型
final class MenuActivityChartModel: ObservableObject {
    @Published var points: [(hour: Int, count: Int)] = []
}

/// 状态栏菜单内的今日分小时活动折线图（键鼠操作次数）
struct MenuActivityChartView: View {
    @ObservedObject var model: MenuActivityChartModel

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("今日分小时活动")
                .font(.system(size: 11, weight: .semibold))
                .foregroundColor(.secondary)

            if model.points.isEmpty {
                Text("暂无数据")
                    .font(.system(size: 11))
                    .foregroundColor(.secondary)
                    .frame(height: 72)
                    .frame(maxWidth: .infinity)
            } else {
                Chart(model.points, id: \.hour) { p in
                    AreaMark(
                        x: .value("时", p.hour),
                        y: .value("次", p.count)
                    )
                    .foregroundStyle(
                        LinearGradient(
                            colors: [
                                DashboardTheme.accentBlue.opacity(0.35),
                                DashboardTheme.accentIndigo.opacity(0.03)
                            ],
                            startPoint: .top,
                            endPoint: .bottom
                        )
                    )
                    LineMark(
                        x: .value("时", p.hour),
                        y: .value("次", p.count)
                    )
                    .foregroundStyle(DashboardTheme.accentBlue)
                    .lineStyle(StrokeStyle(lineWidth: 1.5))
                }
                .chartXScale(domain: 0...23)
                .chartXAxis {
                    AxisMarks(values: [0, 6, 12, 18, 23]) { value in
                        AxisValueLabel {
                            if let hour = value.as(Int.self) {
                                Text("\(hour)时")
                                    .font(.system(size: 8))
                            }
                        }
                        AxisGridLine()
                    }
                }
                .chartYAxis(.hidden)
                .frame(height: 72)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .frame(width: 280)
    }
}
