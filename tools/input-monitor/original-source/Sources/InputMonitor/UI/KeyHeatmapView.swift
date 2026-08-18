import SwiftUI

/// 按键热区：今日按键次数排行（横向条形列表）
struct KeyHeatmapView: View {
    let items: [KeyHeatItem]

    private var maxCount: Int { items.map(\.count).max() ?? 1 }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("按键热区 Top \(items.count)")
                .font(.system(size: 13, weight: .semibold))

            if items.isEmpty {
                Text("暂无按键数据")
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
                    .padding(.vertical, 24)
                    .frame(maxWidth: .infinity)
            } else {
                LazyVStack(spacing: 6) {
                    ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                        HStack(spacing: 10) {
                            Text(displayLabel(item.label))
                                .font(.system(size: 11, weight: .medium, design: .monospaced))
                                .frame(width: 72, alignment: .leading)
                                .lineLimit(1)
                                .help("\(displayLabel(item.label))：\(item.count) 次按键")

                            GeometryReader { geo in
                                RoundedRectangle(cornerRadius: 3)
                                    .fill(
                                        LinearGradient(
                                            colors: [
                                                DashboardTheme.accentBlue,
                                                DashboardTheme.accentIndigo
                                            ],
                                            startPoint: .leading,
                                            endPoint: .trailing
                                        )
                                    )
                                    .frame(width: max(2, geo.size.width * CGFloat(item.count) / CGFloat(maxCount)))
                            }
                            .frame(height: 12)

                            Text("\(item.count)")
                                .font(.system(size: 11, design: .monospaced))
                                .foregroundColor(.secondary)
                                .frame(width: 52, alignment: .trailing)
                        }
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .dashboardCard()
    }

    private func displayLabel(_ label: String) -> String {
        if label.hasPrefix("key:"),
           let code = Int(label.dropFirst(4)) {
            return Self.keyCodeNames[code] ?? "其他按键"
        }
        switch label {
        case " ": return "空格"
        case "\t": return "Tab"
        default: return label
        }
    }

    /// macOS 虚拟键码 → 友好名称（无字符输出的功能键）
    private static let keyCodeNames: [Int: String] = [
        36: "回车", 76: "小键盘回车",
        48: "Tab", 49: "空格",
        51: "退格", 117: "向前删除",
        53: "Esc",
        123: "←", 124: "→", 125: "↓", 126: "↑",
        115: "Home", 119: "End", 116: "PgUp", 121: "PgDn",
        122: "F1", 120: "F2", 99: "F3", 118: "F4",
        96: "F5", 97: "F6", 98: "F7", 100: "F8",
        101: "F9", 109: "F10", 103: "F11", 111: "F12",
        50: "`",
        55: "⌘", 56: "⇧", 58: "⌥", 59: "⌃",
        57: "Caps Lock",
        71: "Clear", 67: "Num +", 69: "Num +",
        75: "Num /", 78: "Num -", 81: "Num =", 82: "Num 0",
        83: "Num 1", 84: "Num 2", 85: "Num 3", 86: "Num 4",
        87: "Num 5", 88: "Num 6", 89: "Num 7",
        91: "Num 8", 92: "Num 9",
        65: "Num .",
        114: "Insert", 105: "F13", 107: "F14", 113: "F15",
        106: "F16", 64: "F17", 79: "F18", 80: "F19", 90: "F20"
    ]
}
