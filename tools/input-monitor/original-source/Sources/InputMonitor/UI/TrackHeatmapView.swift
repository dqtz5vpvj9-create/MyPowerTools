import AppKit
import SwiftUI

/// 鼠标轨迹热力：按屏幕分辨率归一化网格聚合渲染，悬浮显示格子密度
struct TrackHeatmapView: View {
    let points: [(x: Double, y: Double)]
    /// 标题日期文案（默认"今日"，日期筛选时传入如"8月1日"）
    var dayLabel: String = "今日"

    private let gridCols = 48
    private let gridRows = 27

    @State private var hoverCell: (col: Int, row: Int, location: CGPoint)?

    private var screenSize: CGSize {
        NSScreen.main?.frame.size ?? CGSize(width: 1920, height: 1080)
    }

    /// 网格聚合（Canvas 渲染与悬浮探测共用）
    private var grid: (counts: [Int], maxCount: Int) {
        var counts = [Int](repeating: 0, count: gridCols * gridRows)
        let screen = screenSize
        for p in points {
            let col = Int(p.x / screen.width * Double(gridCols))
            let row = Int(p.y / screen.height * Double(gridRows))
            guard (0..<gridCols).contains(col), (0..<gridRows).contains(row) else { continue }
            counts[row * gridCols + col] += 1
        }
        return (counts, max(counts.max() ?? 1, 1))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("\(dayLabel)鼠标轨迹热力")
                .font(.system(size: 13, weight: .semibold))

            if points.isEmpty {
                Text("暂无轨迹数据")
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
                    .padding(.vertical, 40)
                    .frame(maxWidth: .infinity)
            } else {
                let gridData = grid
                Canvas { context, size in
                    let cellW = size.width / CGFloat(gridCols)
                    let cellH = size.height / CGFloat(gridRows)
                    for row in 0..<gridRows {
                        for col in 0..<gridCols {
                            let count = gridData.counts[row * gridCols + col]
                            guard count > 0 else { continue }
                            let t = min(1, Double(count) / Double(gridData.maxCount))
                            let color = DashboardTheme.accentIndigo.opacity(0.12 + 0.75 * t)
                            let rect = CGRect(
                                x: CGFloat(col) * cellW,
                                y: CGFloat(row) * cellH,
                                width: cellW,
                                height: cellH
                            )
                            context.fill(Path(roundedRect: rect, cornerRadius: 2), with: .color(color))
                        }
                    }
                }
                .frame(height: 260)
                .background(DashboardTheme.insetBackground, in: RoundedRectangle(cornerRadius: 8))
                .overlay(hoverProbe(maxCount: gridData.maxCount))
                .overlay(hoverBadge(counts: gridData.counts))

                Text("采样 \(points.count) 个点 · 颜色越深表示经过越频繁")
                    .font(.system(size: 10))
                    .foregroundColor(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .dashboardCard()
    }

    /// 悬浮探测层
    private func hoverProbe(maxCount: Int) -> some View {
        GeometryReader { geo in
            Rectangle()
                .fill(.clear)
                .contentShape(Rectangle())
                .onContinuousHover { phase in
                    switch phase {
                    case .active(let location):
                        let col = Int(location.x / geo.size.width * CGFloat(gridCols))
                        let row = Int(location.y / geo.size.height * CGFloat(gridRows))
                        if (0..<gridCols).contains(col), (0..<gridRows).contains(row) {
                            hoverCell = (col, row, location)
                        } else {
                            hoverCell = nil
                        }
                    case .ended:
                        hoverCell = nil
                    }
                }
        }
    }

    /// 悬浮数值气泡
    private func hoverBadge(counts: [Int]) -> some View {
        GeometryReader { geo in
            if let cell = hoverCell {
                let count = counts[cell.row * gridCols + cell.col]
                if count > 0 {
                    Text("\(count) 次")
                        .font(.system(size: 10, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                        .padding(.horizontal, 7)
                        .padding(.vertical, 3)
                        .background(DashboardTheme.cardBackground, in: Capsule())
                        .overlay(Capsule().strokeBorder(Color.primary.opacity(0.1), lineWidth: 1))
                        .shadow(color: .black.opacity(0.1), radius: 4, y: 2)
                        .position(
                            x: min(max(cell.location.x, 30), geo.size.width - 30),
                            y: max(cell.location.y - 22, 12)
                        )
                }
            }
        }
        .allowsHitTesting(false)
    }
}
