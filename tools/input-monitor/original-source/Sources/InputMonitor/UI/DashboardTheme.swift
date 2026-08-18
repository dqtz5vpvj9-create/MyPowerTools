import AppKit
import SwiftUI

/// 看板主题：浅色/深色跟随系统外观的语义化颜色 + 统一卡片容器样式
enum DashboardTheme {
    /// 动态颜色（跟随系统外观切换）
    static func dynamic(light: NSColor, dark: NSColor) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
        })
    }

    // MARK: - 背景

    /// 页面底色
    static let pageBackground = dynamic(
        light: NSColor(red: 0.957, green: 0.965, blue: 0.976, alpha: 1), // #F4F6F9
        dark: NSColor(red: 0.106, green: 0.106, blue: 0.114, alpha: 1)   // #1B1B1D
    )

    /// 卡片底色
    static let cardBackground = dynamic(
        light: .white,
        dark: NSColor(red: 0.169, green: 0.169, blue: 0.176, alpha: 1)   // #2B2B2D
    )

    /// 卡片内嵌区域底色（如轨迹画布）
    static let insetBackground = dynamic(
        light: NSColor(red: 0.957, green: 0.961, blue: 0.969, alpha: 1), // #F4F5F7
        dark: NSColor(white: 0, alpha: 0.22)
    )

    // MARK: - 辅助元素

    /// 空热力格
    static let heatEmpty = dynamic(
        light: NSColor(red: 0.914, green: 0.922, blue: 0.937, alpha: 1), // #E9EBEF
        dark: NSColor(white: 1, alpha: 0.09)
    )

    /// 浅灰标签胶囊底色
    static let tagBackground = dynamic(
        light: NSColor(white: 0.94, alpha: 1),
        dark: NSColor(white: 1, alpha: 0.10)
    )

    /// 中性分类色（other）
    static let neutral = dynamic(
        light: NSColor(white: 0.70, alpha: 1),
        dark: NSColor(white: 0.55, alpha: 1)
    )

    // MARK: - 强调色（两种外观下均保证对比度）

    static let accentBlue = dynamic(
        light: NSColor(red: 0.04, green: 0.52, blue: 1.00, alpha: 1),
        dark: NSColor(red: 0.22, green: 0.62, blue: 1.00, alpha: 1)
    )
    static let accentIndigo = dynamic(
        light: NSColor(red: 0.37, green: 0.36, blue: 0.90, alpha: 1),
        dark: NSColor(red: 0.49, green: 0.48, blue: 0.96, alpha: 1)
    )
    static let accentGreen = dynamic(
        light: NSColor(red: 0.16, green: 0.76, blue: 0.31, alpha: 1),
        dark: NSColor(red: 0.24, green: 0.84, blue: 0.38, alpha: 1)
    )

    // MARK: - 热力梯度

    /// 热力颜色：低→高单色梯度，浅色/深色分别标定端点
    static func heatColor(value: Double, max maxValue: Double) -> Color {
        guard maxValue > 0, value > 0 else { return heatEmpty }
        let t = min(1, value / maxValue)
        func mix(_ from: (Double, Double, Double), _ to: (Double, Double, Double)) -> NSColor {
            NSColor(
                red: from.0 + (to.0 - from.0) * t,
                green: from.1 + (to.1 - from.1) * t,
                blue: from.2 + (to.2 - from.2) * t,
                alpha: 1
            )
        }
        return dynamic(
            light: mix((0.86, 0.91, 1.00), (0.37, 0.36, 0.90)),  // #DCE9FF → #5E5CE6
            dark: mix((0.22, 0.21, 0.34), (0.48, 0.46, 0.97))    // 深紫底 → 亮紫
        )
    }
}

/// 统一卡片容器：圆角 + 柔和投影（浅色）/ 细提亮描边（深色）
struct DashboardCardStyle: ViewModifier {
    @Environment(\.colorScheme) private var colorScheme

    func body(content: Content) -> some View {
        content
            .background(DashboardTheme.cardBackground, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(
                        colorScheme == .dark ? Color.white.opacity(0.07) : Color.black.opacity(0.04),
                        lineWidth: 1
                    )
            )
            .shadow(
                color: Color.black.opacity(colorScheme == .dark ? 0.3 : 0.05),
                radius: colorScheme == .dark ? 3 : 10,
                x: 0,
                y: colorScheme == .dark ? 1 : 3
            )
    }
}

extension View {
    func dashboardCard() -> some View {
        modifier(DashboardCardStyle())
    }
}

/// 区块标题
struct DashboardSectionTitle: View {
    let text: String

    init(_ text: String) {
        self.text = text
    }

    var body: some View {
        Text(text)
            .font(.system(size: 13, weight: .semibold))
            .foregroundColor(.primary)
    }
}
